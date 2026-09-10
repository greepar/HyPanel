namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;
using HyPanel.Shared.Contracts;

internal static class AdminHealthSummaryEndpoints
{
    private static readonly TimeSpan OnlineThreshold = TimeSpan.FromSeconds(30);

    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/api/admin/v1/health-summary", GetAsync);

    private static async Task<IResult> GetAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, TimeProvider timeProvider, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var now = timeProvider.GetUtcNow();
        var nodes = await repository.GetNodeObservationsAsync(ct);
        var services = await repository.GetHealthServicesAsync(ct);
        var response = Build(now, nodes, services);
        return Results.Json(response, ServerJsonSerializerContext.Default.HealthSummaryResponse);
    }

    internal static HealthSummaryResponse Build(
        DateTimeOffset observedAtUtc,
        IReadOnlyList<NodeObservationRecord> nodes,
        IReadOnlyList<HealthServiceRecord> services)
    {
        var names = nodes.ToDictionary(static node => node.Id, static node => node.DisplayName);
        var issues = new List<HealthSummaryIssue>();
        var online = 0;
        var drifted = 0;
        foreach (var node in nodes)
        {
            var isOnline = node.LastSeenAtUtc is { } seen && seen >= observedAtUtc - OnlineThreshold;
            if (isOnline) online++;
            if (node.AgentId is not null && !isOnline)
                issues.Add(new HealthSummaryIssue("offline", node.Id, node.DisplayName, null, null, null, null));
            if (node.AgentId is not null &&
                (node.AppliedRevision is null || node.DesiredRevision != node.AppliedRevision))
            {
                drifted++;
                issues.Add(new HealthSummaryIssue("revisionDrift", node.Id, node.DisplayName, null, null, null, null));
            }
        }

        var running = 0;
        var failed = 0;
        foreach (var service in services)
        {
            if (service.RuntimeStatus == (int)ServiceRuntimeStatus.Running) running++;
            if (service.RuntimeStatus == (int)ServiceRuntimeStatus.Failed)
            {
                failed++;
                issues.Add(new HealthSummaryIssue("failedService", service.NodeId, names[service.NodeId], service.Id,
                    service.Name, service.ErrorCode, service.ErrorMessage));
            }
        }

        var counts = new HealthSummaryCounts(nodes.Count, online, drifted, services.Count, running, failed,
            services.Count - running - failed);
        return new HealthSummaryResponse(observedAtUtc, counts, issues.ToArray());
    }
}