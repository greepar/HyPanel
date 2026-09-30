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
        endpoints.MapGet("/api/admin/v1/egress-nodes", ListEgressAsync);
        endpoints.MapGet("/api/admin/v1/egress-backends", ListEgressBackendsAsync);
        endpoints.MapPut("/api/admin/v1/nodes/{nodeId:guid}/egress", SetEgressAsync);
        endpoints.MapPut("/api/admin/v1/nodes/{nodeId:guid}/agent-update-policy", SetUpdatePolicyAsync);
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/agent-update", RequestUpdateAsync);
        endpoints.MapPost("/api/admin/v1/nodes/agent-update", RequestBatchUpdateAsync);
        endpoints.MapDelete("/api/admin/v1/nodes/{nodeId:guid}", DeleteAsync);
    }

    private static IResult EgressError(int status, string message) =>
        Results.Json(new EgressErrorResponse(message), ServerJsonSerializerContext.Default.EgressErrorResponse,
            statusCode: status);

    private static async Task<IResult> ListEgressBackendsAsync(HttpRequest request, AdminAuthorization authorization,
        CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Results.Json(HyPanel.Shared.Contracts.EgressTransports.Available.ToArray(),
            ServerJsonSerializerContext.Default.EgressTransportDefinitionArray);
    }

    private static async Task<IResult> ListEgressAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Results.Json((await repository.GetEgressNodesAsync(ct)).ToArray(),
            ServerJsonSerializerContext.Default.EgressNodeRecordArray);
    }

    private static async Task<IResult> SetEgressAsync(Guid nodeId, SetEgressRequest body, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, HyPanel.Server.Security.ProxyCredentialProtector protector, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!HyPanel.Shared.Contracts.EgressTransports.Available.Any(b => b.Id == body.Transport))
            return EgressError(400, "该出口后端尚未实现。");
        if (body.Enabled && body.Transport == HyPanel.Shared.Contracts.EgressTransports.WireGuard && !protector.IsConfigured)
            return EgressError(400, "请先为面板配置持久的 HYPANEL_MASTER_KEY，再启用 WireGuard 出口。");
        var port = body.Transport switch
        {
            HyPanel.Shared.Contracts.EgressTransports.GreUdp => body.UdpPort ?? HyPanel.Shared.Contracts.EgressTransports.GreUdpPort,
            HyPanel.Shared.Contracts.EgressTransports.WireGuard => body.UdpPort ?? HyPanel.Shared.Contracts.EgressTransports.WireGuardPort,
            _ => 0
        };
        if (body.Transport != HyPanel.Shared.Contracts.EgressTransports.Gre && port is < 1 or > 65535)
            return EgressError(400, "UDP 端口必须在 1 到 65535 之间。");
        var node = (await repository.GetNodeObservationsAsync(ct)).FirstOrDefault(n => n.Id == nodeId);
        if (node is null) return Results.NotFound();
        if (body.Enabled && !(await repository.GetEgressNodesAsync(ct)).Any(n => n.Id == nodeId && n.Supported && n.SupportedTransports.Contains(body.Transport)))
            return EgressError(400, "请先更新该节点的 Agent，等待它重新上报后再启用出口。");
        if (body.Enabled && (node.ReportedPlatform?.StartsWith("linux-", StringComparison.Ordinal) != true
            || node.PublicIpv4 is null))
            return EgressError(400, "出口转发需要已接入的 Linux 节点和公网 IPv4。");
        return await repository.SetEgressEnabledAsync(nodeId, body.Enabled, ct, body.Transport, port)
            ? Results.NoContent() : EgressError(409, "该出口仍被服务使用，请先切换这些服务的出口。");
    }

    private static async Task<IResult> DeleteAsync(Guid nodeId, HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if ((await repository.GetEgressNodesAsync(cancellationToken)).Any(n => n.Id == nodeId && n.UsedBy > 0))
            return EgressError(409, "该节点仍被服务用作出口，请先切换这些服务的出口。");
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
