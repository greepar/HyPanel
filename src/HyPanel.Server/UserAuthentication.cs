namespace HyPanel.Server;

using Microsoft.Net.Http.Headers;
using HyPanel.Server.Persistence;

internal sealed class UserAuthentication(SqliteServerRepository repository)
{
    public async Task<UserRecord?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out var value) || value.Count != 1) return null;
        var header = value[0];
        return header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7
            ? await repository.AuthenticateSessionAsync(header[7..], cancellationToken) : null;
    }
    public static string? GetBearer(HttpRequest request) => request.Headers.TryGetValue(HeaderNames.Authorization, out var value) && value.Count == 1 && value[0] is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header.Length > 7 ? header[7..] : null;
}
