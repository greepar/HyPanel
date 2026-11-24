namespace HyPanel.Shared.Contracts;

public sealed record AgentSyncRequest(
    string AgentVersion,
    string Platform,
    long AppliedRevision,
    NodeMetrics Metrics,
    IReadOnlyList<ServiceRuntimeState> Services,
    IReadOnlyList<UsageBatch> UsageBatches,
    IReadOnlyList<AgentCommandResult> CommandResults,
    AgentUpdateReport? AgentUpdate = null,
    string? PublicIpv4 = null,
    string? CountryCode = null,
    bool SupportsAvx = false,
    IReadOnlyList<BackendArtifact>? AppliedBackendArtifacts = null,
    string? PublicIpv6 = null,
    Guid? LogStreamId = null,
    IReadOnlyList<ServiceLogChunk>? ServiceLogs = null,
    IReadOnlyList<AcmeChallengeReport>? AcmeChallenges = null);

public sealed record AgentSyncResponse(
    long DesiredRevision,
    NodeDesiredState? DesiredState,
    IReadOnlyList<AgentCommand> Commands,
    IReadOnlyList<Guid> AcceptedUsageBatchIds,
    int SyncIntervalSeconds,
    AgentUpdateDescriptor? AgentUpdate = null,
    string? PanelUrl = null,
    IReadOnlyList<PortRelayRule>? Relays = null,
    IReadOnlyList<ServiceLogCursor>? LogCursors = null,
    IReadOnlyList<AcmeChallengeAssignment>? AcmeChallenges = null);

/// <summary>
/// An ACME challenge the Panel is answering for a certificate it obtains, which the Agent proves on the Panel's behalf:
/// <c>http-01</c> serves <see cref="KeyAuthorization"/> at <c>http://Domain/.well-known/acme-challenge/Token</c> on
/// port 80; <c>tls-alpn-01</c> presents the challenge certificate to <c>acme-tls/1</c> clients on TCP 443. The list is
/// the full set: a challenge no longer listed is withdrawn.
/// </summary>
public sealed record AcmeChallengeAssignment(string Type, string Domain, string Token, string KeyAuthorization);

/// <summary>Whether the Agent answers a challenge it was assigned; <see cref="Error"/> says why not.</summary>
public sealed record AcmeChallengeReport(string Token, bool Ready, string? Error);

/// <summary>
/// New output lines of one service, numbered by the Agent. <see cref="FirstSequence"/> is the number of the first
/// line; the rest follow without gaps. Numbers restart whenever <see cref="AgentSyncRequest.LogStreamId"/> changes
/// (the Agent process restarted).
/// </summary>
public sealed record ServiceLogChunk(Guid ServiceId, long FirstSequence, IReadOnlyList<string> Lines);

/// <summary>
/// The last line number of a service the Panel holds for the Agent's current log stream. A Panel that knows nothing
/// of the stream (it restarted) answers with no cursors, so the Agent sends its whole buffer again. Null means the
/// Panel does not take live logs (older Panel).
/// </summary>
public sealed record ServiceLogCursor(Guid ServiceId, long Sequence);

/// <summary>Answer of the Agent's long-poll watch: true when the Panel has something new for the next sync.</summary>
public sealed record AgentWatchResponse(bool Changed);

/// <summary>
/// A TCP port this node forwards to the node that really runs the service. Sent on every sync, separately from the
/// desired state, because the target address can change without a revision change. Null means the Panel did not
/// say (older Panel); an empty list means no relays.
/// </summary>
public sealed record PortRelayRule(Guid ServiceId, int ListenPort, string TargetHost, int TargetPort);

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
    long NetworkDownloadBytes,
    IReadOnlyList<int>? ListeningTcpPorts = null,
    IReadOnlyList<int>? ListeningUdpPorts = null);

public sealed record ServiceDesiredState(
    Guid ServiceId,
    string Name,
    string BackendType,
    string BackendVersion,
    bool Enabled,
    int ConfigSchemaVersion,
    string ConfigJson,
    IReadOnlyList<BackendUser>? Users = null,
    int? ControlPort = null,
    TlsCertificateAsset? TlsCertificate = null);

/// <summary>
/// TLS material for a service. <c>Upload</c> ships PEM from the Panel; <c>Path</c> points at files already on the
/// node; <c>Acme</c> lets the backend issue and renew the certificate itself.
/// </summary>
public sealed record TlsCertificateAsset(
    Guid CertificateId,
    string Fingerprint,
    string? CertificatePem,
    string? PrivateKeyPem,
    string Kind = TlsCertificateKinds.Upload,
    string? CertificatePath = null,
    string? PrivateKeyPath = null,
    IReadOnlyList<string>? AcmeDomains = null,
    string? AcmeEmail = null,
    string? AcmeChallenge = null,
    string? AcmeDnsToken = null);

public static class TlsCertificateKinds
{
    public const string Upload = "Upload";
    public const string Path = "Path";
    public const string Acme = "Acme";
}

public static class AcmeChallenges
{
    /// <summary>HTTP-01 on TCP 80.</summary>
    public const string Http = "http";
    /// <summary>TLS-ALPN-01 on TCP 443.</summary>
    public const string Tls = "tls";
    /// <summary>DNS-01 through the Cloudflare API; needs no inbound port.</summary>
    public const string Cloudflare = "cloudflare";
}

/// <param name="DirectOnly">
/// The user's group may not use upstream proxies: the Agent leaves out the lines (default or route) that leave through
/// one. The Panel already drops a user who would have no line left.
/// </param>
public sealed record BackendUser(
    Guid UserId,
    string Credential,
    bool DirectOnly = false)
{
    /// <summary>Whether this user may take a line; <paramref name="proxied"/> says the line leaves through a proxy.</summary>
    public bool Admits(bool proxied) => !(proxied && DirectOnly);
}

public sealed record BackendArtifact(
    string BackendType,
    string Version,
    string Rid,
    string FileName,
    string Sha256,
    long Size,
    bool RequiresAvx = false);

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
    Restarting = 8,
    Backoff = 9,
}

public sealed record ServiceRuntimeState(
    Guid ServiceId,
    ServiceRuntimeStatus Status,
    string? BackendVersion,
    string? AppliedConfigSha256,
    BackendTrafficSnapshot? Traffic,
    DateTimeOffset ObservedAt,
    string? ErrorCode,
    string? ErrorMessage,
    string? ExitCountryCode = null,
    IReadOnlyList<RouteExitCountry>? RouteExitCountries = null);

/// <summary>The country an extra line of a service exits from through its own upstream proxy.</summary>
public sealed record RouteExitCountry(Guid RouteId, string CountryCode);

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
