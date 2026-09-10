namespace HyPanel.Agent.Backends;

using HyPanel.Shared.Contracts;

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

    ValueTask<BackendTrafficSnapshot?> CollectTrafficAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken);
}

[Flags]
public enum BackendCapabilities
{
    None = 0,
    Users = 1 << 0,
    TrafficStats = 1 << 1,
    HotReload = 1 << 2,
    Logs = 1 << 3,
    VersionQuery = 1 << 4,
    MultiInbound = 1 << 5,
    ConfigValidation = 1 << 6,
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
