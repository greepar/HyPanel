using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace HyPanel.Server;

internal sealed class AdminTokenAuthentication
{
    private const string DevelopmentOverrideKey = "HyPanel:AllowInsecureDevelopmentAdminToken";
    private readonly byte[] tokenHash;

    public AdminTokenAuthentication(IConfiguration configuration, IHostEnvironment environment)
    {
        var token = configuration["HYPANEL_ADMIN_TOKEN"];
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("HYPANEL_ADMIN_TOKEN must be configured.");
        }

        var allowsInsecureDevelopmentToken = environment.IsDevelopment()
            && configuration.GetValue<bool>(DevelopmentOverrideKey);
        if (!allowsInsecureDevelopmentToken && (token.Length < 32 || Encoding.UTF8.GetByteCount(token) < 32))
        {
            throw new InvalidOperationException("HYPANEL_ADMIN_TOKEN must contain at least 32 characters and 32 UTF-8 bytes.");
        }

        tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    public bool IsAuthenticated(HttpRequest request)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out var authorization)
            || authorization.Count != 1)
        {
            return false;
        }

        var value = authorization[0];
        if (string.IsNullOrEmpty(value)
            || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return Matches(value[7..]);
    }

    public bool Matches(string? token) =>
        !string.IsNullOrEmpty(token) &&
        CryptographicOperations.FixedTimeEquals(tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
