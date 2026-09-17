namespace HyPanel.Server.Endpoints;

using System.Text.Json;
using System.Text;
using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using HyPanel.Shared.Versioning;

internal static class AgentSyncEndpoints
{
    private const int MaximumAgentVersionLength = 128;
    private const int MaximumPlatformLength = 128;
    private const int MaximumCommandResults = 100;
    private const int MaximumErrorCodeLength = 128;
    private const int MaximumErrorMessageLength = 1024;
    private const int MaximumCommandOutputBytes = 65536;
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
        ReleaseCatalog releaseCatalog,
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

        await repository.RecordAgentUpdateReportAsync(agent.AgentId, request.AgentUpdate, cancellationToken);
        await repository.RefreshCredentialEligibilityAsync(agent.AgentId, cancellationToken);

        var desired = await repository.GetDesiredStateForAgentAsync(agent.AgentId, cancellationToken);
        if (desired is null)
        {
            return Results.Unauthorized();
        }

        var commands = await repository.GetActiveCommandsAsync(agent.AgentId, cancellationToken);
        var responseCommands = new AgentCommand[commands.Count];
        for (var index = 0; index < commands.Count; index++)
        {
            var command = commands[index];
            responseCommands[index] = new AgentCommand(
                command.Id,
                command.Type == "CollectServiceLogs" ? AgentCommandType.CollectServiceLogs : AgentCommandType.RunHealthCheck,
                command.CreatedAtUtc,
                command.ExpiresAtUtc,
                command.TargetServiceId);
        }

        var update = await GetAgentUpdateAsync(agent.AgentId, request, repository, releaseCatalog, cancellationToken);
        NodeDesiredState? desiredState = null;
        if (request.AppliedRevision != desired.Value.Revision)
        {
            var desiredServices = new List<ServiceDesiredState>(desired.Value.Services.Count);
            var controlPort = 20_000;
            foreach (var service in desired.Value.Services)
            {
                var users = service.BackendType == "xray"
                    ? (await repository.GetServiceCredentialsAsync(service.Id, true, cancellationToken))
                        .Select(item => new BackendUser(item.UserId, item.Credential)).ToArray()
                    : Array.Empty<BackendUser>();
                desiredServices.Add(new ServiceDesiredState(service.Id, service.Name, service.BackendType,
                    service.BackendVersion, service.Enabled, service.ConfigSchemaVersion, service.ConfigJson, users,
                    service.BackendType == "xray" ? controlPort++ : null));
            }
            desiredState = new NodeDesiredState(desired.Value.Revision, desiredServices,
                desired.Value.Services.Where(service => service.Enabled).Select(service =>
                        backendArtifactCatalog.FindArtifact(service.BackendType, service.BackendVersion,
                            request.Platform.Trim()))
                    .Where(static artifact => artifact is not null).Cast<BackendArtifact>().ToArray());
        }
        var response = new AgentSyncResponse(
            desired.Value.Revision,
            desiredState,
            responseCommands,
            request.UsageBatches.Select(batch => batch.BatchId).ToArray(),
            SyncIntervalSeconds,
            update);
        return Results.Json(response, HyPanelJsonSerializerContext.Default.AgentSyncResponse);
    }

    internal static async Task<AgentUpdateDescriptor?> GetAgentUpdateAsync(Guid agentId, AgentSyncRequest request,
        SqliteServerRepository repository, ReleaseCatalog catalog, CancellationToken cancellationToken)
    {
        var target = await repository.GetAgentUpdateTargetAsync(agentId, cancellationToken);
        var manifest = catalog.Manifest;
        if (target is null || manifest is null
            || !SemanticVersion.TryParse(request.AgentVersion, out var current)) return null;
        var desired = target.DesiredVersion;
        var updateId = target.UpdateId;
        if (AgentUpdatePlanner.ShouldRequestAutoUpdate(target.Policy, request.AgentVersion, manifest.Version, desired))
        {
            desired = manifest.Version;
            updateId = Guid.NewGuid();
            await repository.RequestAgentUpdateAsync(target.NodeId, desired, updateId.Value, cancellationToken);
        }
        if (desired is null || updateId is null || request.AgentVersion == desired
            || !SemanticVersion.TryParse(desired, out var desiredVersion) || current.CompareTo(desiredVersion) >= 0) return null;
        var asset = catalog.FindAsset(desired, request.Platform);
        return asset is null ? null : new AgentUpdateDescriptor(updateId.Value, desired, asset.Rid, asset.FileName,
            asset.Sha256, asset.Size);
    }

    private static bool IsValidRequest(AgentSyncRequest request, DateTimeOffset nowUtc)
    {
        if (!AdminNodesEndpoints.TryNormalize(request.AgentVersion, MaximumAgentVersionLength, out _)
            || !SemanticVersion.TryParse(request.AgentVersion, out _)
            || !AdminNodesEndpoints.TryNormalize(request.Platform, MaximumPlatformLength, out _)
            || !ReleaseCatalog.SupportedRids.Contains(request.Platform, StringComparer.Ordinal)
            || request.AppliedRevision < 0
            || request.Metrics is null
            || request.Services is null
            || request.Services.Count > 128
            || request.CommandResults is null
            || request.CommandResults.Count > MaximumCommandResults
            || request.UsageBatches is null
            || request.UsageBatches.Count > 32
            || !IsValidMetrics(request.Metrics, nowUtc)
            || !IsValidUpdateReport(request.AgentUpdate, request.Platform, nowUtc))
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

    private static bool IsValidUpdateReport(AgentUpdateReport? report, string reportedRid, DateTimeOffset nowUtc) => report is null
        || report.UpdateId != Guid.Empty && Enum.IsDefined(report.Status)
        && SemanticVersion.TryParse(report.TargetVersion, out _)
        && report.Rid == reportedRid
        && report.StartedAt <= nowUtc + MaximumFutureClockSkew
        && (report.PreviousVersion is null || SemanticVersion.TryParse(report.PreviousVersion, out _))
        && HasMaximumLength(report.LastError, MaximumErrorMessageLength);

    private static bool IsValidServiceRuntimeState(ServiceRuntimeState state, DateTimeOffset nowUtc) =>
        state.ServiceId != Guid.Empty && Enum.IsDefined(state.Status) && state.ObservedAt <= nowUtc + MaximumFutureClockSkew
        && HasMaximumLength(state.BackendVersion, MaximumAgentVersionLength) && IsOptionalSha256(state.AppliedConfigSha256)
        && HasMaximumLength(state.ErrorCode, MaximumErrorCodeLength) && HasMaximumLength(state.ErrorMessage, MaximumErrorMessageLength)
        && (state.Traffic is null || (state.Traffic.UploadBytes >= 0 && state.Traffic.DownloadBytes >= 0 && state.Traffic.ObservedAt <= nowUtc + MaximumFutureClockSkew));

    internal static bool IsValidMetrics(NodeMetrics metrics, DateTimeOffset nowUtc) =>
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
            || !HasMaximumLength(result.ErrorMessage, MaximumErrorMessageLength)
            || result.Output is not null && Encoding.UTF8.GetByteCount(result.Output) > MaximumCommandOutputBytes)
        {
            return false;
        }

        return result.Status switch
        {
            AgentCommandStatus.Running => result.CompletedAt is null
                && result.ErrorCode is null
                && result.ErrorMessage is null
                && result.Output is null,
            AgentCommandStatus.Succeeded => result.CompletedAt is { } completedAt
                && completedAt >= result.StartedAt
                && completedAt <= nowUtc + MaximumFutureClockSkew
                && result.ErrorCode is null
                && result.ErrorMessage is null,
            AgentCommandStatus.Failed => result.CompletedAt is { } completedAt
                && completedAt >= result.StartedAt
                && completedAt <= nowUtc + MaximumFutureClockSkew
                && result.Output is null,
            _ => false,
        };
    }

    private static bool HasMaximumLength(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength;

    private static bool IsOptionalSha256(string? value) =>
        value is null || value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
