namespace HyPanel.Server.Endpoints;

using System.Text.Json;
using HyPanel.Server.Persistence;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using HyPanel.Server.Releases;

internal static class AdminObservationEndpoints
{
    private static readonly TimeSpan OnlineThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthCheckCommandLifetime = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/nodes", ListNodesAsync);
        endpoints.MapPost("/api/admin/v1/agents/{agentId:guid}/commands/health-check", CreateHealthCheckCommandAsync);
    }

    private static async Task<IResult> ListNodesAsync(
        HttpRequest httpRequest,
        AdminAuthorization authorization,
        SqliteServerRepository repository,
        ReleaseCatalog releaseCatalog,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);

        var nowUtc = timeProvider.GetUtcNow();
        var observations = await repository.GetNodeObservationsAsync(cancellationToken);
        var response = new AdminNodeObservationResponse[observations.Count];
        var latest = releaseCatalog.Manifest?.Version;
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            response[index] = new AdminNodeObservationResponse(
                observation.Id,
                observation.DisplayName,
                observation.AgentId,
                observation.LastSeenAtUtc is { } lastSeenAt && lastSeenAt >= nowUtc - OnlineThreshold,
                observation.LastSeenAtUtc,
                observation.ReportedVersion,
                observation.ReportedPlatform,
                observation.DesiredRevision,
                observation.AppliedRevision,
                TryReadMetrics(observation.LatestMetricSnapshotJson, nowUtc),
                observation.AgentUpdatePolicy,
                latest,
                observation.DesiredAgentVersion,
                GetUpdateStatus(observation, latest, nowUtc),
                observation.UpdateError);
        }

        return Results.Json(response, ServerJsonSerializerContext.Default.AdminNodeObservationResponseArray);
    }

    private static string GetUpdateStatus(NodeObservationRecord observation, string? latest, DateTimeOffset nowUtc)
    {
        if (observation.UpdateStatus == "Failed") return "Failed";
        if (observation.DesiredAgentVersion is { } desired && observation.ReportedVersion != desired)
            return observation.UpdateStatus is "Downloading" or "Staged" or "Applying" ? observation.UpdateStatus
                : observation.LastSeenAtUtc is null || observation.LastSeenAtUtc < nowUtc - OnlineThreshold
                    ? "WaitingForReconnect" : "Requested";
        if (observation.DesiredAgentVersion is { } completed && observation.ReportedVersion == completed) return "Succeeded";
        if (latest is not null && observation.ReportedVersion is { } current
            && HyPanel.Shared.Versioning.SemanticVersion.TryParse(current, out var currentVersion)
            && HyPanel.Shared.Versioning.SemanticVersion.TryParse(latest, out var latestVersion)
            && currentVersion.CompareTo(latestVersion) < 0) return "Available";
        return "UpToDate";
    }

    /// <summary>
    /// The latest metric snapshot is written by the Agent sync path. A missing or unreadable snapshot is treated as
    /// "no telemetry yet" so one bad row can never fail the whole observation listing.
    /// </summary>
    internal static NodeMetrics? TryReadMetrics(string? snapshotJson, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(snapshotJson))
        {
            return null;
        }

        try
        {
            var metrics = JsonSerializer.Deserialize(snapshotJson, HyPanelJsonSerializerContext.Default.NodeMetrics);
            var current = now ?? TimeProvider.System.GetUtcNow();
            return metrics is not null && AgentSyncEndpoints.IsValidMetrics(metrics, current) ? metrics : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<IResult> CreateHealthCheckCommandAsync(
        Guid agentId,
        HttpRequest httpRequest,
        AdminAuthorization authorization,
        SqliteServerRepository repository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);

        var expiresAtUtc = timeProvider.GetUtcNow() + HealthCheckCommandLifetime;
        var command = await repository.CreateRunHealthCheckCommandAsync(
            Guid.NewGuid(),
            agentId,
            expiresAtUtc,
            cancellationToken);
        if (command is null)
        {
            return Results.NotFound();
        }

        var response = new CreateHealthCheckCommandResponse(command.Id);
        return Results.Json(
            response,
            ServerJsonSerializerContext.Default.CreateHealthCheckCommandResponse,
            statusCode: StatusCodes.Status201Created);
    }
}
