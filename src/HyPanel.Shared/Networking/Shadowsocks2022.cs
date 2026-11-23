using System.Security.Cryptography;
using System.Text;

namespace HyPanel.Shared.Networking;

/// <summary>
/// Shadowsocks 2022 multi-user keys. The server has its own key (iPSK) and every user a key of the cipher's length;
/// clients send <c>serverKey:userKey</c>. A user's key is derived from their credential (a UUID), so the Panel's
/// subscription and the Agent's server config agree without storing a second secret per user.
/// </summary>
public static class Shadowsocks2022
{
    public const string Cipher = "2022-blake3-aes-128-gcm";

    /// <summary>Key length in bytes of <see cref="Cipher"/>.</summary>
    public const int KeyLength = 16;

    public static string UserKey(string credential) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("hypanel-ss2022-user:" + credential))[..KeyLength]);

    /// <summary>True for standard Base64 of exactly <see cref="KeyLength"/> bytes.</summary>
    public static bool IsKey(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        var buffer = new byte[KeyLength + 3];
        return Convert.TryFromBase64String(value, buffer, out var written) && written == KeyLength;
    }
}
