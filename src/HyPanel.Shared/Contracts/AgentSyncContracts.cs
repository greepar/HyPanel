namespace HyPanel.Shared.Contracts;

public sealed record AgentSyncRequest(
    string AgentVersion,
    string Platform,
    long AppliedRevision,
    NodeMetrics Metrics,
    IReadOnlyList<ServiceRuntimeState> Services,
    IReadOnlyList<UsageBatch> UsageBatches,
    IReadOnlyList<AgentCommandResult> CommandResults,
    AgentUpdateReport? AgentUpdate = null);

public sealed record AgentSyncResponse(
    long DesiredRevision,
    NodeDesiredState? DesiredState,
    IReadOnlyList<AgentCommand> Commands,
    IReadOnlyList<Guid> AcceptedUsageBatchIds,
    int SyncIntervalSeconds,
    AgentUpdateDescriptor? AgentUpdate = null);

public enum AgentUpdateStatus
{
    Idle = 0,
    Downloading = 1,
    Staged = 2,
    Applying = 3,
    RestartPending = 4,
    Verifying = 5,
    Succeeded = 6,
    Failed = 7,
}

public sealed record AgentUpdateDescriptor(
    Guid UpdateId,
    string Version,
    string Rid,
    string FileName,
    string Sha256,
    long Size);

public sealed record AgentUpdateReport(
    Guid UpdateId,
    AgentUpdateStatus Status,
    string TargetVersion,
    string Rid,
    DateTimeOffset StartedAt,
    string? PreviousVersion,
    string? LastError);

public sealed record NodeDesiredState(
    long Revision,
    IReadOnlyList<ServiceDesiredState> Services,
    IReadOnlyList<BackendArtifact> BackendArtifacts);

public sealed record NodeMetrics(
    DateTimeOffset ObservedAt,
    long UptimeSeconds,
    double CpuUsagePercent,
    long MemoryTotalBytes,
    long MemoryAvailableBytes,
    long DiskTotalBytes,
    long DiskAvailableBytes,
    long NetworkUploadBytes,
    long NetworkDownloadBytes);

public sealed record ServiceDesiredState(
    Guid ServiceId,
    string Name,
    string BackendType,
    string BackendVersion,
    bool Enabled,
    int ConfigSchemaVersion,
    string ConfigJson,
    IReadOnlyList<BackendUser>? Users = null,
    int? ControlPort = null);

public sealed record BackendUser(
    Guid UserId,
    string Credential);

public sealed record BackendArtifact(
    string BackendType,
    string Version,
    string Rid,
    string FileName,
    string Sha256,
    long Size);

public enum ServiceRuntimeStatus
{
    Unknown = 0,
    Installing = 1,
    Stopped = 2,
    Starting = 3,
    Running = 4,
    Stopping = 5,
    Failed = 6,
    Updating = 7,
}

public sealed record ServiceRuntimeState(
    Guid ServiceId,
    ServiceRuntimeStatus Status,
    string? BackendVersion,
    string? AppliedConfigSha256,
    BackendTrafficSnapshot? Traffic,
    DateTimeOffset ObservedAt,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record BackendTrafficSnapshot(
    long UploadBytes,
    long DownloadBytes,
    DateTimeOffset ObservedAt);

public sealed record UsageBatch(
    Guid BatchId,
    DateTimeOffset ObservedAt,
    IReadOnlyList<UserUsageDelta> Records);

public sealed record UserUsageDelta(
    Guid UserId,
    Guid ServiceId,
    long UploadBytes,
    long DownloadBytes);
