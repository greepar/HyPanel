namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;
using HyPanel.Shared.Versioning;

internal static class AdminNodesEndpoints
{
    private const int MaximumDisplayNameLength = 128;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/admin/v1/nodes", CreateAsync);
        endpoints.MapPut("/api/admin/v1/nodes/{nodeId:guid}/agent-update-policy", SetUpdatePolicyAsync);
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/agent-update", RequestUpdateAsync);
        endpoints.MapPost("/api/admin/v1/nodes/agent-update", RequestBatchUpdateAsync);
        endpoints.MapDelete("/api/admin/v1/nodes/{nodeId:guid}", DeleteAsync);
    }

    private static async Task<IResult> DeleteAsync(Guid nodeId, HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await repository.DeleteNodeAsync(nodeId, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> SetUpdatePolicyAsync(Guid nodeId, SetAgentUpdatePolicyRequest request,
        HttpRequest httpRequest, AdminAuthorization authorization, SqliteServerRepository repository,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (request.Policy is not ("Manual" or "Auto")) return Results.BadRequest();
        return await repository.SetAgentUpdatePolicyAsync(nodeId, request.Policy, cancellationToken)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RequestUpdateAsync(Guid nodeId, HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, ReleaseCatalog catalog,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var manifest = catalog.Manifest;
        if (manifest is null) return Results.Conflict();
        var observation = (await repository.GetNodeObservationsAsync(cancellationToken))
            .SingleOrDefault(item => item.Id == nodeId);
        if (observation?.AgentId is null) return Results.NotFound();
        if (!AgentUpdatePlanner.CanRequestManualUpdate(observation.ReportedVersion, manifest.Version))
            return Results.Conflict();
        var updateId = Guid.NewGuid();
        if (!await repository.RequestAgentUpdateAsync(nodeId, manifest.Version, updateId, cancellationToken))
            return Results.NotFound();
        return Results.Json(new RequestAgentUpdateResponse(updateId, manifest.Version),
            ServerJsonSerializerContext.Default.RequestAgentUpdateResponse, statusCode: StatusCodes.Status202Accepted);
    }


    private static async Task<IResult> RequestBatchUpdateAsync(BatchAgentUpdateRequest request, HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, ReleaseCatalog catalog,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var manifest = catalog.Manifest;
        if (manifest is null || request.NodeIds is null || request.NodeIds.Length is < 1 or > 100
            || request.NodeIds.Distinct().Count() != request.NodeIds.Length) return Results.BadRequest();
        var latest = SemanticVersion.Parse(manifest.Version);
        var eligible = (await repository.GetNodeObservationsAsync(cancellationToken))
            .Where(item => request.NodeIds.Contains(item.Id) && item.AgentId is not null
                && SemanticVersion.TryParse(item.ReportedVersion, out var current) && current.CompareTo(latest) < 0)
            .ToArray();
        var count = 0;
        foreach (var node in eligible)
            if (await repository.RequestAgentUpdateAsync(node.Id, manifest.Version, Guid.NewGuid(), cancellationToken)) count++;
        return Results.Json(new BatchAgentUpdateResponse(manifest.Version, count),
            ServerJsonSerializerContext.Default.BatchAgentUpdateResponse);
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
