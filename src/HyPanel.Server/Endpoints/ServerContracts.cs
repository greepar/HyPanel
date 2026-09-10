namespace HyPanel.Server.Endpoints;

internal sealed record CreateNodeRequest(string DisplayName);

internal sealed record CreateNodeResponse(Guid Id, string DisplayName, DateTimeOffset CreatedAtUtc);

internal sealed record CreateEnrollmentTokenResponse(string Token, DateTimeOffset ExpiresAtUtc);

internal sealed record AdminNodeObservationResponse(
    Guid Id,
    string DisplayName,
    Guid? AgentId,
    bool Online,
    DateTimeOffset? LastSeenAt,
    string? ReportedVersion,
    string? Platform,
    long DesiredRevision,
    long? AppliedRevision);

internal sealed record CreateHealthCheckCommandResponse(Guid CommandId);

internal sealed record InstallCommandRequest(string Platform);

internal sealed record InstallCommandResponse(string Command);

internal sealed record CreateServiceRequest(
    string Name,
    string BackendType,
    string BackendVersion,
    int ConfigSchemaVersion,
    string ConfigJson);

internal sealed record UpdateServiceRequest(
    string Name,
    string BackendVersion,
    bool Enabled,
    int ConfigSchemaVersion,
    string ConfigJson);

internal sealed record SetServiceEnabledRequest(bool Enabled);

internal sealed record ServiceMutationResponse(Guid Id, long Revision);

internal sealed record ServiceRuntimeResponse(
    int Status,
    string? BackendVersion,
    string? AppliedConfigSha256,
    long? TrafficUploadBytes,
    long? TrafficDownloadBytes,
    DateTimeOffset? TrafficObservedAtUtc,
    DateTimeOffset ObservedAtUtc,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed record AdminServiceResponse(
    Guid Id,
    string Name,
    string BackendType,
    string BackendVersion,
    bool Enabled,
    int ConfigSchemaVersion,
    string ConfigJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    ServiceRuntimeResponse? Runtime);

internal sealed record LoginRequest(string Username, string Password);

internal sealed record UserResponse(
    Guid Id,
    string Username,
    string Role,
    bool Enabled,
    long? TrafficLimitBytes,
    DateTimeOffset? ExpiresAtUtc);

internal sealed record LoginResponse(string Token, DateTimeOffset ExpiresAtUtc, UserResponse User);

internal sealed record CreateUserRequest(
    string Username,
    string Password,
    string Role,
    bool Enabled,
    long? TrafficLimitBytes,
    DateTimeOffset? ExpiresAtUtc);

internal sealed record UpdateUserRequest(
    string Username,
    string? Password,
    string Role,
    bool Enabled,
    long? TrafficLimitBytes,
    DateTimeOffset? ExpiresAtUtc);

internal sealed record CreateUserResponse(UserResponse User, string SubscriptionToken);

internal sealed record RotateSubscriptionTokenResponse(string SubscriptionToken);

internal sealed record UsageTotalResponse(
    Guid UserId,
    Guid ServiceId,
    long UploadBytes,
    long DownloadBytes,
    DateTimeOffset UpdatedAtUtc);

internal sealed record ServicePublicEndpointRequest(string Host, int Port, string? TlsServerName);

internal sealed record ServicePublicEndpointResponse(
    string Host,
    int Port,
    string? TlsServerName,
    DateTimeOffset UpdatedAtUtc);

internal sealed record CreateServiceTemplateRequest(
    string Name,
    string BackendType,
    string BackendVersion,
    int ConfigSchemaVersion,
    string ConfigJson);

internal sealed record UpdateServiceTemplateRequest(
    string Name,
    string BackendType,
    string BackendVersion,
    int ConfigSchemaVersion,
    string ConfigJson);

internal sealed record ServiceTemplateResponse(
    Guid Id,
    string Name,
    string BackendType,
    string BackendVersion,
    int ConfigSchemaVersion,
    string ConfigJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record CreateServiceFromTemplateRequest(string Name);

internal sealed record BatchServiceEnabledItem(Guid NodeId, Guid ServiceId, bool Enabled);

internal sealed record BatchServiceEnabledRequest(BatchServiceEnabledItem[] Items);

internal sealed record BatchServiceEnabledNodeResponse(Guid NodeId, long Revision);

internal sealed record BatchServiceEnabledResponse(BatchServiceEnabledNodeResponse[] Nodes);

internal sealed record HealthSummaryCounts(
    int NodesTotal,
    int NodesOnline,
    int NodesDrifted,
    int ServicesTotal,
    int ServicesRunning,
    int ServicesFailed,
    int ServicesStoppedOrUnknown);

internal sealed record HealthSummaryIssue(
    string Kind,
    Guid NodeId,
    string NodeDisplayName,
    Guid? ServiceId,
    string? ServiceName,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed record HealthSummaryResponse(
    DateTimeOffset ObservedAtUtc,
    HealthSummaryCounts Counts,
    HealthSummaryIssue[] Issues);