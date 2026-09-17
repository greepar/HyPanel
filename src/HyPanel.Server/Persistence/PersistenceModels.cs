namespace HyPanel.Server.Persistence;

internal sealed record NodeRecord(Guid Id, string DisplayName, long DesiredRevision, DateTimeOffset CreatedAtUtc);

internal sealed record AgentRecord(
    Guid Id,
    Guid NodeId,
    DateTimeOffset EnrolledAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    string? ReportedVersion,
    string? ReportedPlatform,
    long AppliedRevision,
    string? LatestMetricSnapshotJson);

internal sealed record EnrollmentTokenRecord(
    Guid Id,
    Guid NodeId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    DateTimeOffset? ConsumedAtUtc,
    Guid? ConsumedByAgentId);

internal sealed record AgentCommandRecord(
    Guid Id,
    Guid AgentId,
    string Type,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorCode,
    string? ErrorMessage,
    DateTimeOffset? ExpiresAtUtc,
    Guid? TargetServiceId,
    string? Output);

internal sealed record EnrollmentTokenIssue(EnrollmentTokenRecord Token, string PlaintextToken);

internal sealed record AgentEnrollmentResult(Guid AgentId, Guid NodeId);

internal sealed record AgentAuthenticationRecord(Guid AgentId, Guid NodeId, byte[] SecretHash);

internal sealed record NodeObservationRecord(
    Guid Id,
    string DisplayName,
    long DesiredRevision,
    Guid? AgentId,
    DateTimeOffset? LastSeenAtUtc,
    string? ReportedVersion,
    string? ReportedPlatform,
    long? AppliedRevision,
    string? LatestMetricSnapshotJson = null,
    string AgentUpdatePolicy = "Manual",
    string? DesiredAgentVersion = null,
    Guid? AgentUpdateId = null,
    string? UpdateStatus = null,
    string? UpdateTargetVersion = null,
    DateTimeOffset? UpdateStartedAtUtc = null,
    string? UpdateError = null);

internal sealed record AgentUpdateTargetRecord(
    Guid NodeId,
    Guid AgentId,
    string ReportedVersion,
    string ReportedRid,
    string Policy,
    string? DesiredVersion,
    Guid? UpdateId);

internal sealed record ServiceInstanceRecord(
    Guid Id,
    Guid NodeId,
    string Name,
    string BackendType,
    string BackendVersion,
    bool Enabled,
    int ConfigSchemaVersion,
    string ConfigJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string BackendUpdatePolicy = "Manual");

internal sealed record BackendUpdateTargetRecord(Guid NodeId, Guid ServiceId, string BackendType,
    string DesiredVersion, string ReportedRid, string Policy);
internal sealed record GlobalSettingsRecord(string AgentUpdateDefaultPolicy, string BackendUpdateDefaultPolicy,
    string? GithubMirrorBaseUrl, DateTimeOffset UpdatedAtUtc);

internal sealed record ServiceRuntimeStateRecord(
    Guid ServiceId,
    int Status,
    string? BackendVersion,
    string? AppliedConfigSha256,
    long? TrafficUploadBytes,
    long? TrafficDownloadBytes,
    DateTimeOffset? TrafficObservedAtUtc,
    DateTimeOffset ObservedAtUtc,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed record ServiceInstanceWithRuntimeRecord(ServiceInstanceRecord Service, ServiceRuntimeStateRecord? Runtime);

internal sealed record ServiceTemplateRecord(
    Guid Id,
    string Name,
    string NormalizedName,
    string BackendType,
    string BackendVersion,
    int ConfigSchemaVersion,
    string ConfigJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record HealthServiceRecord(
    Guid Id,
    Guid NodeId,
    string Name,
    int? RuntimeStatus,
    string? ErrorCode,
    string? ErrorMessage);

internal sealed record BatchServiceEnabledItemRecord(Guid NodeId, Guid ServiceId, bool Enabled);
internal sealed record BatchServiceEnabledNodeResult(Guid NodeId, long Revision);

internal sealed record ServicePublicEndpointRecord(
    Guid ServiceId,
    string Host,
    int Port,
    string? TlsServerName,
    DateTimeOffset UpdatedAtUtc);

internal sealed record SubscriptionServiceRecord(
    Guid UserId,
    Guid ServiceId,
    string Name,
    string BackendType,
    string ConfigJson,
    string Credential,
    ServicePublicEndpointRecord PublicEndpoint);
