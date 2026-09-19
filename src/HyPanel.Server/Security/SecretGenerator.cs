namespace HyPanel.Server.Security;

using System.Security.Cryptography;

/// <summary>
/// Generates fresh default secrets for newly created service instances. Values are only returned to the requesting
/// Admin and are stored exactly like operator-entered values.
/// </summary>
internal static class SecretGenerator
{
    public static string Base64Url(int byteCount) => ToBase64Url(RandomNumberGenerator.GetBytes(byteCount));

    public static string StandardBase64(int byteCount) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount));

    public static string LowerHex(int byteCount) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(byteCount)).ToLowerInvariant();

    public static (string PrivateKey, string PublicKey) RealityKeyPair()
    {
        var privateKey = X25519.ClampScalar(RandomNumberGenerator.GetBytes(32));
        var publicKey = X25519.ScalarMultiplyBase(privateKey);
        return (ToBase64Url(privateKey), ToBase64Url(publicKey));
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
