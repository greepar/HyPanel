using HyPanel.Shared.Contracts;

namespace HyPanel.Server.Endpoints;

internal sealed record ApiErrorResponse(string Error);

internal sealed record CreateNodeRequest(string DisplayName);

internal sealed record ProxyTestStartRequest(string? Type, string? Host, int? Port, string? Username, string? Password);

internal sealed record ProxyTestStartResponse(Guid Id);

/// <summary>A proxy test: <see cref="Done"/> once the node answered, then the outcome.</summary>
internal sealed record ProxyTestStatusResponse(bool Done, bool Succeeded, string? Ip, string? CountryCode, string? City,
    int? LatencyMs, string? Error);

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
    long? AppliedRevision,
    NodeMetrics? Metrics,
    string AgentUpdatePolicy,
    string? LatestAgentVersion,
    string? DesiredAgentVersion,
    string AgentUpdateStatus,
    string? AgentUpdateError,
    string? PublicIpv4,
    string? CountryCode = null,
    string? PublicIpv6 = null,
    long TrafficUploadBytes = 0,
    long TrafficDownloadBytes = 0,
    int? TrafficResetDay = null,
    DateTimeOffset? TrafficPeriodStart = null,
    string? DetectedCity = null,
    string? CityOverride = null);

internal sealed record SetNodeCityRequest(string? City);

internal sealed record SetNodeTrafficResetDayRequest(int? Day);

internal sealed record SetAgentUpdatePolicyRequest(string Policy);
internal sealed record RequestAgentUpdateResponse(Guid UpdateId, string Version);
internal sealed record BatchAgentUpdateRequest(Guid[] NodeIds);
internal sealed record BatchAgentUpdateResponse(string Version, int UpdatedNodes);

internal sealed record CreateHealthCheckCommandResponse(Guid CommandId);

internal sealed record CreateDiagnosticCommandResponse(Guid CommandId);

internal sealed record ServiceDiagnosticResponse(
    Guid CommandId,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? ErrorCode,
    string? ErrorMessage,
    string? Output);

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
    string ConfigJson,
    string? BackendType = null);

internal sealed record SetServiceEnabledRequest(bool Enabled);
internal sealed record SetBackendUpdatePolicyRequest(string Policy);
internal sealed record RequestBackendUpdateResponse(string Version, long Revision);
internal sealed record ServerUpdateRequestResponse(string Version);

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
    ServiceRuntimeResponse? Runtime,
    string BackendUpdatePolicy,
    string? LatestBackendVersion,
    bool Ipv6Entry = true);

/// <summary><see cref="Remember"/> asks for a long-lived session ("stay signed in").</summary>
internal sealed record LoginRequest(string Username, string Password, bool Remember = false);

internal sealed record SetupStatusResponse(bool NeedsSetup);

/// <summary>Creates the first administrator; authorised by the install-time HYPANEL_ADMIN_TOKEN.</summary>
internal sealed record SetupRequest(string Token, string Username, string Password);

internal sealed record UserResponse(
    Guid Id,
    string Username,
    string Role,
    bool Enabled,
    long? TrafficLimitBytes,
    DateTimeOffset? ExpiresAtUtc,
    Guid? GroupId = null,
    int? TrafficResetDay = null);

internal sealed record LoginResponse(string Token, DateTimeOffset ExpiresAtUtc, UserResponse User);

/// <summary>WebAuthn ceremony options; binary values are base64url. User fields are set only for registration.</summary>
internal sealed record PasskeyOptionsResponse(string ChallengeId, string Challenge, string RpId, string RpName,
    string? UserHandle, string? UserName, string[] ExcludeCredentialIds);

internal sealed record PasskeyRegisterRequest(string ChallengeId, string? Name, string CredentialId,
    string ClientDataJson, string AuthenticatorData, string PublicKey, int Algorithm);

internal sealed record PasskeyLoginRequest(string ChallengeId, string CredentialId, string ClientDataJson,
    string AuthenticatorData, string Signature, string? UserHandle, bool Remember = false);

internal sealed record PasskeyResponse(string Id, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastUsedAtUtc);

internal sealed record CreateUserRequest(
    string Username,
    string Password,
    string Role,
    bool Enabled,
    long? TrafficLimitBytes,
    DateTimeOffset? ExpiresAtUtc,
    Guid? GroupId = null,
    int? TrafficResetDay = null);

internal sealed record UpdateUserRequest(
    string Username,
    string? Password,
    string Role,
    bool Enabled,
    long? TrafficLimitBytes,
    DateTimeOffset? ExpiresAtUtc,
    Guid? GroupId = null,
    int? TrafficResetDay = null);

internal sealed record UserGroupRequest(string Name, bool AutoIncludeNewServices, Guid[]? ServiceIds,
    bool DirectOnly = false);
internal sealed record UserGroupResponse(Guid Id, string Name, bool IsDefault, bool AutoIncludeNewServices,
    Guid[] ServiceIds, int MemberCount, bool DirectOnly);

internal sealed record CreateUserResponse(UserResponse User, string SubscriptionToken);

internal sealed record RotateSubscriptionTokenResponse(string SubscriptionToken, string? SubscriptionUrl = null);
internal sealed record UserServiceAccessResponse(Guid ServiceId, string CredentialStatus, bool MultiUser,
    bool PerUserTraffic, bool TrafficStats);

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

internal sealed record PreviousUsagePeriodResponse(Guid UserId, DateTimeOffset StartUtc, DateTimeOffset EndUtc,
    PreviousUsageEntryResponse[] Entries);

internal sealed record PreviousUsageEntryResponse(Guid ServiceId, string NodeName, string ServiceName, string? CountryCode,
    long UploadBytes, long DownloadBytes);

/// <summary>The signed-in user's usage per node and service: this period, and the one their last reset closed.</summary>
internal sealed record OwnUsageBreakdownResponse(PreviousUsageEntryResponse[] Current, PreviousUsagePeriodResponse? Previous);

/// <summary>The newest Agent release the Panel knows after checking; <see cref="Error"/> when GitHub could not be reached.</summary>
internal sealed record AgentReleaseCheckResponse(string? LatestVersion, string? Error);
