using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;

namespace HyPanel.Agent.Backends;

public interface IBackendProvider
{
    string BackendType { get; }

    BackendCapabilities Capabilities { get; }

    ValueTask<BackendValidationResult> ValidateAsync(
        ServiceDesiredState desiredState,
        CancellationToken cancellationToken);

    ValueTask<RenderedBackendConfig> RenderConfigAsync(
        ServiceDesiredState desiredState,
        CancellationToken cancellationToken);

    ValueTask<BackendProcessSpec> CreateProcessSpecAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken);

    ValueTask<BackendHealthResult> CheckHealthAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<BackendUserTraffic>> CollectUserTrafficAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken);
}

/// <summary>
/// A backend whose services can listen on extra UDP ports that the Agent redirects to the listen port
/// (see <see cref="Infrastructure.PortHoppingManager"/>).
/// </summary>
public interface IPortHoppingBackend
{
    /// <summary>The listen port and the hopping ports of a service config, or null when it has none.</summary>
    (int ListenPort, PortSpec Ports)? PortHopping(string configJson);
}

[Flags]
public enum BackendCapabilities
{
    None = 0,
    MultiUser = 1 << 0,
    TrafficStats = 1 << 1,
    HotReload = 1 << 2,
    Logs = 1 << 3,
    VersionQuery = 1 << 4,
    MultiInbound = 1 << 5,
    ConfigValidation = 1 << 6,
    PerUserTraffic = 1 << 7,
}

public sealed record BackendValidationResult(
    bool IsValid,
    IReadOnlyList<int> TcpPorts,
    IReadOnlyList<int> UdpPorts,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record RenderedBackendConfig(
    string FileName,
    ReadOnlyMemory<byte> Content,
    string Sha256);

public sealed record BackendInstanceContext(
    ServiceDesiredState DesiredState,
    string InstanceDirectory,
    string BinaryPath,
    string ConfigPath);

public sealed record BackendProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);

public sealed record BackendHealthResult(
    bool IsHealthy,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record BackendUserTraffic(
    Guid UserId,
    long UploadBytes,
    long DownloadBytes,
    DateTimeOffset ObservedAt);
