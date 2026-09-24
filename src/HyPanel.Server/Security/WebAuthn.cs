namespace HyPanel.Server.Security;

using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Minimal WebAuthn (passkey) relying party. Registration requests no attestation and takes the public key from
/// the browser's <c>getPublicKey()</c> (SubjectPublicKeyInfo DER), so no CBOR/COSE parsing is needed; assertions
/// are verified against that key. Supports ES256 (-7) and RS256 (-257).
/// </summary>
internal sealed class WebAuthn(TimeProvider time)
{
    public const int Es256 = -7;
    public const int Rs256 = -257;
    private const int MaxPendingChallenges = 2048;
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Challenge> _challenges = new();

    private sealed record Challenge(byte[] Value, Guid? UserId, DateTimeOffset ExpiresAtUtc);

    /// <summary>Issues a single-use challenge; returns null when too many are pending (anonymous flood).</summary>
    public (string Id, string Value)? CreateChallenge(Guid? userId)
    {
        var now = time.GetUtcNow();
        foreach (var (key, pending) in _challenges)
            if (pending.ExpiresAtUtc <= now) _challenges.TryRemove(key, out _);
        if (_challenges.Count >= MaxPendingChallenges) return null;
        var value = RandomNumberGenerator.GetBytes(32);
        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        _challenges[id] = new Challenge(value, userId, now + ChallengeLifetime);
        return (id, Base64Url.EncodeToString(value));
    }

    /// <summary>Consumes the challenge; it must exist, be unexpired and belong to <paramref name="userId"/>.</summary>
    public byte[]? TakeChallenge(string? id, Guid? userId) =>
        id is not null && _challenges.TryRemove(id, out var pending) && pending.ExpiresAtUtc > time.GetUtcNow() &&
        pending.UserId == userId
            ? pending.Value
            : null;

    /// <summary>RP id is the host the panel is reached on (forwarded headers are honoured).</summary>
    public static string RpId(HttpRequest request) => request.Host.Host.ToLowerInvariant();

    public static byte[]? Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return Base64Url.DecodeFromChars(value); }
        catch (FormatException) { return null; }
    }

    public static string Encode(ReadOnlySpan<byte> value) => Base64Url.EncodeToString(value);

    /// <summary>Checks type, challenge and origin of <c>clientDataJSON</c>.</summary>
    public static bool VerifyClientData(byte[] clientDataJson, string type, byte[] challenge, string rpId)
    {
        try
        {
            using var document = JsonDocument.Parse(clientDataJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeValue) || typeValue.GetString() != type ||
                !root.TryGetProperty("challenge", out var challengeValue) ||
                Decode(challengeValue.GetString()) is not { } received ||
                !CryptographicOperations.FixedTimeEquals(received, challenge) ||
                !root.TryGetProperty("origin", out var originValue) ||
                !Uri.TryCreate(originValue.GetString(), UriKind.Absolute, out var origin)) return false;
            return string.Equals(origin.Host, rpId, StringComparison.OrdinalIgnoreCase) &&
                   (origin.Scheme == Uri.UriSchemeHttps || rpId == "localhost");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks the RP id hash and user-presence flag, and returns the signature counter. When
    /// <paramref name="credentialId"/> is given the attested credential data must carry that id.
    /// </summary>
    public static bool VerifyAuthenticatorData(byte[] authData, string rpId, byte[]? credentialId, out uint signCount)
    {
        signCount = 0;
        if (authData.Length < 37 ||
            !CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), SHA256.HashData(Encoding.UTF8.GetBytes(rpId))) ||
            (authData[32] & 0x01) == 0) return false;
        signCount = BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));
        if (credentialId is null) return true;
        // Attested credential data: aaguid(16) | idLength(2) | id.
        if ((authData[32] & 0x40) == 0 || authData.Length < 55) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(53, 2));
        return authData.Length >= 55 + length && authData.AsSpan(55, length).SequenceEqual(credentialId);
    }

    public static bool IsSupportedKey(byte[] publicKey, int algorithm)
    {
        try
        {
            switch (algorithm)
            {
                case Es256:
                    using (var ecdsa = ECDsa.Create())
                    {
                        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                        return ecdsa.KeySize == 256;
                    }
                case Rs256:
                    using (var rsa = RSA.Create())
                    {
                        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                        return rsa.KeySize >= 2048;
                    }
                default:
                    return false;
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Verifies an assertion signature over <c>authenticatorData || SHA-256(clientDataJSON)</c>.</summary>
    public static bool VerifySignature(byte[] publicKey, int algorithm, byte[] authData, byte[] clientDataJson,
        byte[] signature)
    {
        var signed = new byte[authData.Length + 32];
        authData.CopyTo(signed, 0);
        SHA256.HashData(clientDataJson, signed.AsSpan(authData.Length));
        try
        {
            switch (algorithm)
            {
                case Es256:
                    using (var ecdsa = ECDsa.Create())
                    {
                        ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                        return ecdsa.VerifyData(signed, signature, HashAlgorithmName.SHA256,
                            DSASignatureFormat.Rfc3279DerSequence);
                    }
                case Rs256:
                    using (var rsa = RSA.Create())
                    {
                        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                        return rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    }
                default:
                    return false;
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
