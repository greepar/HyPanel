using HyPanel.Server.Live;
using HyPanel.Server.Persistence;
using HyPanel.Server.Proxies;
using HyPanel.Shared.Networking;
using HyPanel.Shared.Versioning;

namespace HyPanel.Server.Endpoints;

/// <summary>
/// Checks an upstream proxy from a node before it is saved: the node looks up its address through the proxy and
/// reports the exit IP, country, city and latency, or why the proxy did not work.
/// </summary>
internal static class AdminProxyTestEndpoints
{
    /// <summary>The first Agent that runs proxy tests.</summary>
    internal const string MinimumAgentVersion = "0.5.40";

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/proxy-tests", StartAsync);
        endpoints.MapGet("/api/admin/v1/nodes/{nodeId:guid}/proxy-tests/{testId:guid}", GetAsync);
    }

    private static async Task<IResult> StartAsync(Guid nodeId, ProxyTestStartRequest body, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, ProxyTestBoard board, ChangeFeed changeFeed,
        CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!UpstreamProxy.TryCreate(body.Type, body.Host?.Trim(), body.Port, body.Username, body.Password,
                out var proxy, out var error) || proxy is null)
            return Error(error ?? "请先填写代理类型、地址和端口。", StatusCodes.Status400BadRequest);
        if (await repository.GetNodeAsync(nodeId, ct) is null) return Results.NotFound();
        var version = await repository.GetNodeAgentVersionAsync(nodeId, ct);
        if (version is null) return Error("这个节点的 Agent 还没有连上面板。", StatusCodes.Status409Conflict);
        if (SemanticVersion.TryParse(version, out var current) && SemanticVersion.TryParse(MinimumAgentVersion, out var minimum)
            && current.CompareTo(minimum) < 0)
            return Error($"这个节点的 Agent 是 {version}，需要更新到 {MinimumAgentVersion} 或更新版本才能检测代理。",
                StatusCodes.Status409Conflict);
        var id = board.Post(nodeId, proxy.Type, proxy.Host, proxy.Port, proxy.Username, proxy.Password);
        // Wake the node so it picks the test up now rather than at its next scheduled sync.
        await repository.WakeNodesAsync([nodeId], ct);
        changeFeed.Bump();
        return Results.Json(new ProxyTestStartResponse(id), ServerJsonSerializerContext.Default.ProxyTestStartResponse,
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> GetAsync(Guid nodeId, Guid testId, HttpRequest request,
        AdminAuthorization authorization, ProxyTestBoard board, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var (found, report) = board.Get(nodeId, testId);
        if (!found) return Results.NotFound();
        var response = report is null
            ? new ProxyTestStatusResponse(false, false, null, null, null, null, null)
            : new ProxyTestStatusResponse(true, report.Succeeded, report.Ip, report.CountryCode, report.City,
                report.LatencyMs, report.Error);
        return Results.Json(response, ServerJsonSerializerContext.Default.ProxyTestStatusResponse);
    }

    private static IResult Error(string message, int status) =>
        Results.Json(new ApiErrorResponse(message), ServerJsonSerializerContext.Default.ApiErrorResponse,
            statusCode: status);
}
