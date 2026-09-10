namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;

internal static class AdminNodesEndpoints
{
    private const int MaximumDisplayNameLength = 128;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/admin/v1/nodes", CreateAsync);
    }

    private static async Task<IResult> CreateAsync(
        HttpRequest httpRequest,
        CreateNodeRequest request,
        AdminAuthorization authorization,
        SqliteServerRepository repository,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);

        if (!TryNormalize(request.DisplayName, MaximumDisplayNameLength, out var displayName))
        {
            return Results.BadRequest();
        }

        var node = await repository.CreateNodeAsync(Guid.NewGuid(), displayName, cancellationToken);
        var response = new CreateNodeResponse(node.Id, node.DisplayName, node.CreatedAtUtc);
        return Results.Json(response, ServerJsonSerializerContext.Default.CreateNodeResponse,
            statusCode: StatusCodes.Status201Created);
    }

    internal static bool TryNormalize(string? value, int maximumLength, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        normalized = value.Trim();
        return normalized.Length <= maximumLength && !ContainsControlCharacter(normalized);
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }
}