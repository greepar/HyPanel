namespace HyPanel.Server.Endpoints;

using System.Text.Json;
using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

internal static class AgentSyncEndpoints
{
    private const int MaximumAgentVersionLength = 128;
    private const int MaximumPlatformLength = 128;
    private const int MaximumCommandResults = 100;
    private const int MaximumErrorCodeLength = 128;
    private const int MaximumErrorMessageLength = 1024;
    private const int SyncIntervalSeconds = 8;
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/agent/v1/sync", SyncAsync);
    }

    private static async Task<IResult> SyncAsync(
        HttpRequest httpRequest,
        AgentSyncRequest request,
        AgentAuthentication authentication,
        SqliteServerRepository repository,
        BackendArtifactCatalog backendArtifactCatalog,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var agent = await authentication.AuthenticateAsync(httpRequest, cancellationToken);
        if (agent is null)
        {
            return Results.Unauthorized();
        }

        var nowUtc = timeProvider.GetUtcNow();
        if (!IsValidRequest(request, nowUtc))
        {
            return Results.BadRequest();
        }

        var metricSnapshotJson = JsonSerializer.Serialize(request.Metrics, HyPanelJsonSerializerContext.Default.NodeMetrics);
        if (!await repository.TryUpdateAgentSyncAsync(
                agent.AgentId,
                request.AgentVersion.Trim(),
                request.Platform.Trim(),
                request.AppliedRevision,
                metricSnapshotJson,
                request.Services,
                request.CommandResults,
                cancellationToken,
                request.UsageBatches))
        {
            return Results.Unauthorized();
        }

        var desired = await repository.GetDesiredStateForAgentAsync(agent.AgentId, cancellationToken);
        if (desired is null)
        {
            return Results.Unauthorized();
        }

        var commands = await repository.GetActiveHealthCheckCommandsAsync(agent.AgentId, cancellationToken);
        var responseCommands = new AgentCommand[commands.Count];
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            responseCommands[index] = new AgentCommand(
                command.Id,
                AgentCommandType.RunHealthCheck,
                command.CreatedAtUtc,
                command.ExpiresAtUtc);
        }

        var response = new AgentSyncResponse(
            desired.Value.Revision,
            request.AppliedRevision == desired.Value.Revision ? null : new NodeDesiredState(desired.Value.Revision, desired.Value.Services.Select(service => new ServiceDesiredState(service.Id, service.Name, service.BackendType, service.BackendVersion, service.Enabled, service.ConfigSchemaVersion, service.ConfigJson)).ToArray(), backendArtifactCatalog.GetArtifacts(request.Platform.Trim())),
            responseCommands,
            request.UsageBatches.Select(batch => batch.BatchId).ToArray(),
            SyncIntervalSeconds);
        return Results.Json(response, HyPanelJsonSerializerContext.Default.AgentSyncResponse);
    }

    private static bool IsValidRequest(AgentSyncRequest request, DateTimeOffset nowUtc)
    {
        if (!AdminNodesEndpoints.TryNormalize(request.AgentVersion, MaximumAgentVersionLength, out _)
            || !AdminNodesEndpoints.TryNormalize(request.Platform, MaximumPlatformLength, out _)
            || request.AppliedRevision < 0
            || request.Metrics is null
            || request.Services is null
            || request.Services.Count > 128
            || request.CommandResults is null
            || request.CommandResults.Count > MaximumCommandResults
            || request.UsageBatches is null
            || request.UsageBatches.Count > 32
            || !IsValidMetrics(request.Metrics, nowUtc))
        {
            return false;
        }

        foreach (var result in request.CommandResults)
        {
            if (!IsValidCommandResult(result, nowUtc))
            {
                return false;
            }
        }

        var batchIds = new HashSet<Guid>();
        foreach (var batch in request.UsageBatches)
        {
            if (batch.BatchId == Guid.Empty || !batchIds.Add(batch.BatchId) || batch.ObservedAt > nowUtc + MaximumFutureClockSkew || batch.Records is null || batch.Records.Count > 256)
            {
                return false;
            }
            foreach (var record in batch.Records)
            {
                if (record.UserId == Guid.Empty || record.ServiceId == Guid.Empty || record.UploadBytes < 0 || record.DownloadBytes < 0) return false;
            }
        }

        var serviceIds = new HashSet<Guid>();
        foreach (var service in request.Services)
        {
            if (!IsValidServiceRuntimeState(service, nowUtc) || !serviceIds.Add(service.ServiceId)) return false;
        }

        return true;
    }

    private static bool IsValidServiceRuntimeState(ServiceRuntimeState state, DateTimeOffset nowUtc) =>
        state.ServiceId != Guid.Empty && Enum.IsDefined(state.Status) && state.ObservedAt <= nowUtc + MaximumFutureClockSkew
        && HasMaximumLength(state.BackendVersion, MaximumAgentVersionLength) && IsOptionalSha256(state.AppliedConfigSha256)
        && HasMaximumLength(state.ErrorCode, MaximumErrorCodeLength) && HasMaximumLength(state.ErrorMessage, MaximumErrorMessageLength)
        && (state.Traffic is null || (state.Traffic.UploadBytes >= 0 && state.Traffic.DownloadBytes >= 0 && state.Traffic.ObservedAt <= nowUtc + MaximumFutureClockSkew));

    private static bool IsValidMetrics(NodeMetrics metrics, DateTimeOffset nowUtc) =>
        metrics.ObservedAt > nowUtc + MaximumFutureClockSkew
            ? false
            : metrics.UptimeSeconds >= 0
                && metrics.MemoryTotalBytes >= 0
                && metrics.MemoryAvailableBytes >= 0
                && metrics.MemoryAvailableBytes <= metrics.MemoryTotalBytes
                && metrics.DiskTotalBytes >= 0
                && metrics.DiskAvailableBytes >= 0
                && metrics.DiskAvailableBytes <= metrics.DiskTotalBytes
                && metrics.NetworkUploadBytes >= 0
                && metrics.NetworkDownloadBytes >= 0
                && double.IsFinite(metrics.CpuUsagePercent)
                && metrics.CpuUsagePercent is >= 0 and <= 100;

    private static bool IsValidCommandResult(AgentCommandResult result, DateTimeOffset nowUtc)
    {
        if (result.CommandId == Guid.Empty
            || result.StartedAt > nowUtc + MaximumFutureClockSkew
            || !HasMaximumLength(result.ErrorCode, MaximumErrorCodeLength)
            || !HasMaximumLength(result.ErrorMessage, MaximumErrorMessageLength))
        {
            return false;
        }

        return result.Status switch
        {
            AgentCommandStatus.Running => result.CompletedAt is null
                && result.ErrorCode is null
                && result.ErrorMessage is null,
            AgentCommandStatus.Succeeded => result.CompletedAt is { } completedAt
                && completedAt >= result.StartedAt
                && completedAt <= nowUtc + MaximumFutureClockSkew
                && result.ErrorCode is null
                && result.ErrorMessage is null,
            AgentCommandStatus.Failed => result.CompletedAt is { } completedAt
                && completedAt >= result.StartedAt
                && completedAt <= nowUtc + MaximumFutureClockSkew,
            _ => false,
        };
    }

    private static bool HasMaximumLength(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength;

    private static bool IsOptionalSha256(string? value) =>
        value is null || value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
