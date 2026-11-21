using System.Text.Json.Serialization;

namespace HyPanel.Agent.Backends.Xray;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(XrayConfig))]
[JsonSerializable(typeof(XrayHysteria2Config))]
internal sealed partial class XrayJsonSerializerContext : JsonSerializerContext;

internal sealed class XrayConfig : IXrayServiceConfig
{
    public string? UpstreamProxyType { get; init; }

    public string? UpstreamProxyHost { get; init; }

    public int? UpstreamProxyPort { get; init; }

    public string? UpstreamProxyUsername { get; init; }

    public string? UpstreamProxyPassword { get; init; }

    public string? ListenHost { get; init; }

    public int ListenPort { get; init; }

    // Legacy service fields remain parseable during migration; desired users own runtime identities.
    public string? ClientId { get; init; }

    public string? ClientEmail { get; init; }

    public string? Flow { get; init; }

    public string? RealityPrivateKey { get; init; }

    public string? RealityPublicKey { get; init; }

    public string? ShortId { get; init; }

    public string? ServerName { get; init; }

    public string? Destination { get; init; }

    public string? Fingerprint { get; init; }

    /// <summary>Node whose Agent forwards <see cref="RelayPort"/> to this service; only that Agent acts on it.</summary>
    public Guid? RelayNodeId { get; init; }

    public int? RelayPort { get; init; }

    /// <summary>Extra lines on the same port, each with its own exit; see <see cref="HyPanel.Shared.Networking.ServiceRoute"/>.</summary>
    public XrayRouteConfig?[]? Routes { get; init; }
}

internal sealed class XrayRouteConfig : IUpstreamProxyConfig
{
    public Guid Id { get; init; }

    public string? Name { get; init; }

    public string? UpstreamProxyType { get; init; }

    public string? UpstreamProxyHost { get; init; }

    public int? UpstreamProxyPort { get; init; }

    public string? UpstreamProxyUsername { get; init; }

    public string? UpstreamProxyPassword { get; init; }
}

/// <summary>Hysteria2 served by Xray (backend type <c>xray-hysteria2</c>); same fields as the official server's config.</summary>
internal sealed class XrayHysteria2Config : IXrayServiceConfig
{
    public string? UpstreamProxyType { get; init; }

    public string? UpstreamProxyHost { get; init; }

    public int? UpstreamProxyPort { get; init; }

    public string? UpstreamProxyUsername { get; init; }

    public string? UpstreamProxyPassword { get; init; }

    public string? ListenHost { get; init; }

    public int ListenPort { get; init; }

    public Guid CertificateId { get; init; }

    /// <summary>Only used when no user is granted the service.</summary>
    public string? AuthPassword { get; init; }

    public string? MasqueradeUrl { get; init; }

    public string? ObfsPassword { get; init; }

    public int UpMbps { get; init; }

    public int DownMbps { get; init; }

    public string? PortHopping { get; init; }

    public XrayRouteConfig?[]? Routes { get; init; }
}
