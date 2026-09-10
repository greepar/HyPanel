namespace HyPanel.Server;

using System.Security.Cryptography;
using System.Text;
using HyPanel.Server.Persistence;

internal sealed class AgentAuthentication(SqliteServerRepository repository)
{
    public async Task<AgentAuthenticationRecord?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!TryGetAgentId(request, out var agentId) || !TryGetBearerSecret(request, out var secret))
        {
            return null;
        }

        var agent = await repository.GetAgentAuthenticationAsync(agentId, cancellationToken);
        var providedHash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        if (agent is null || !CryptographicOperations.FixedTimeEquals(providedHash, agent.SecretHash))
        {
            return null;
        }

        return agent;
    }

    private static bool TryGetAgentId(HttpRequest request, out Guid agentId) =>
        Guid.TryParse(request.Headers["X-HyPanel-Agent-Id"].ToString(), out agentId);

    private static bool TryGetBearerSecret(HttpRequest request, out string secret)
    {
        secret = string.Empty;
        var authorization = request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.Ordinal) || authorization.Length == prefix.Length)
        {
            return false;
        }

        secret = authorization[prefix.Length..];
        return !string.IsNullOrWhiteSpace(secret);
    }
}
