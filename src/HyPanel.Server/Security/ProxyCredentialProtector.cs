namespace HyPanel.Server.Security;

using System.Security.Cryptography;
using System.Text;

internal sealed record ProtectedCredential(byte[] Nonce, byte[] Ciphertext, byte[] Tag);

internal sealed class ProxyCredentialProtector
{
    private const string ConfigurationKey = "HyPanel:Security:MasterKey";
    private readonly byte[]? key;

    public ProxyCredentialProtector(IConfiguration configuration)
    {
        var encoded = configuration[ConfigurationKey] ?? configuration["HYPANEL_MASTER_KEY"];
        if (string.IsNullOrWhiteSpace(encoded)) return;

        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException($"{ConfigurationKey} must be Base64 encoded.", exception);
        }

        if (key.Length != 32)
            throw new InvalidOperationException($"{ConfigurationKey} must decode to exactly 32 bytes.");
    }

    public bool IsConfigured => key is not null;

    public string? MasterKeyFingerprint
    {
        get
        {
            if (key is null) return null;
            var label = Encoding.UTF8.GetBytes("hypanel:master-key-fingerprint:v1:");
            var value = new byte[label.Length + key.Length];
            label.CopyTo(value, 0);
            key.CopyTo(value, label.Length);
            try { return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant(); }
            finally { CryptographicOperations.ZeroMemory(value); }
        }
    }

    public ProtectedCredential Protect(Guid userId, Guid serviceId, string backendType, string credential)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendType);
        ArgumentException.ThrowIfNullOrWhiteSpace(credential);
        var encryptionKey = RequireKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(credential);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(encryptionKey, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(userId, serviceId, backendType));
        CryptographicOperations.ZeroMemory(plaintext);
        return new ProtectedCredential(nonce, ciphertext, tag);
    }

    public string Unprotect(Guid userId, Guid serviceId, string backendType, ProtectedCredential protectedCredential)
    {
        var plaintext = new byte[protectedCredential.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(RequireKey(), protectedCredential.Tag.Length);
            aes.Decrypt(protectedCredential.Nonce, protectedCredential.Ciphertext, protectedCredential.Tag, plaintext,
                AssociatedData(userId, serviceId, backendType));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("A proxy credential could not be decrypted with the configured master key.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public ProtectedCredential ProtectSubscriptionToken(Guid userId, string token) =>
        ProtectCore(token, Encoding.UTF8.GetBytes($"hypanel:subscription-token:v1:{userId:D}"));

    public string UnprotectSubscriptionToken(Guid userId, ProtectedCredential value) =>
        UnprotectCore(value, Encoding.UTF8.GetBytes($"hypanel:subscription-token:v1:{userId:D}"));

    public ProtectedCredential ProtectAcmeDnsToken(Guid certificateId, string token) =>
        ProtectCore(token, Encoding.UTF8.GetBytes($"hypanel:acme-dns-token:v1:{certificateId:D}"));

    public string UnprotectAcmeDnsToken(Guid certificateId, ProtectedCredential value) =>
        UnprotectCore(value, Encoding.UTF8.GetBytes($"hypanel:acme-dns-token:v1:{certificateId:D}"));

    public ProtectedCredential ProtectCertificateKey(Guid certificateId, string privateKeyPem) =>
        ProtectCore(privateKeyPem, Encoding.UTF8.GetBytes($"hypanel:certificate-key:v1:{certificateId:D}"));

    public string UnprotectCertificateKey(Guid certificateId, ProtectedCredential value) =>
        UnprotectCore(value, Encoding.UTF8.GetBytes($"hypanel:certificate-key:v1:{certificateId:D}"));

    private ProtectedCredential ProtectCore(string value, byte[] associatedData)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(RequireKey(), tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        CryptographicOperations.ZeroMemory(plaintext);
        return new ProtectedCredential(nonce, ciphertext, tag);
    }

    private string UnprotectCore(ProtectedCredential value, byte[] associatedData)
    {
        var plaintext = new byte[value.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(RequireKey(), value.Tag.Length);
            aes.Decrypt(value.Nonce, value.Ciphertext, value.Tag, plaintext, associatedData);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidOperationException("A certificate key could not be decrypted with the configured master key.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private byte[] RequireKey() => key ?? throw new InvalidOperationException(
        $"{ConfigurationKey} is required before proxy credentials can be created or read. Set HYPANEL_MASTER_KEY or HyPanel__Security__MasterKey to a persistent 32-byte Base64 secret.");

    private static byte[] AssociatedData(Guid userId, Guid serviceId, string backendType) =>
        Encoding.UTF8.GetBytes($"hypanel:proxy-credential:v1:{userId:D}:{serviceId:D}:{backendType}");
}
