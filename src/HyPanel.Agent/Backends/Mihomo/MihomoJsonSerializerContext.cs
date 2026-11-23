using System.Text.Json.Serialization;
using HyPanel.Agent.Backends.Xray;

namespace HyPanel.Agent.Backends.Mihomo;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MihomoRealityConfig))]
[JsonSerializable(typeof(MihomoAnyTlsConfig))]
[JsonSerializable(typeof(MihomoShadowsocksConfig))]
internal sealed partial class MihomoJsonSerializerContext : JsonSerializerContext;

/// <summary>Response of Mihomo's <c>GET /connections</c>: process totals and the connections open right now.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(MihomoConnections))]
internal sealed partial class MihomoApiJsonSerializerContext : JsonSerializerContext;

internal sealed class MihomoConnections
{
    public long UploadTotal { get; init; }
    public long DownloadTotal { get; init; }
    public MihomoConnection[]? Connections { get; init; }
}

internal sealed class MihomoConnection
{
    public string? Id { get; init; }
    public long Upload { get; init; }
    public long Download { get; init; }
    public MihomoConnectionMetadata? Metadata { get; init; }
}

internal sealed class MihomoConnectionMetadata
{
    public string? InboundUser { get; init; }
}

/// <summary>The settings every Mihomo-served protocol shares.</summary>
internal interface IMihomoServiceConfig : IUpstreamProxyConfig
{
    string? ListenHost { get; }

    int ListenPort { get; }

    bool? Ipv4Only { get; }

    /// <summary>Extra lines on the same port, each with its own exit (same shape as on Xray).</summary>
    XrayRouteConfig?[]? Routes { get; }
}

/// <summary>VLESS + REALITY on Mihomo; the same fields as the Xray REALITY backend, so services switch engines freely.</summary>
internal sealed class MihomoRealityConfig : IMihomoServiceConfig
{
    public string? UpstreamProxyType { get; init; }
    public string? UpstreamProxyHost { get; init; }
    public int? UpstreamProxyPort { get; init; }
    public string? UpstreamProxyUsername { get; init; }
    public string? UpstreamProxyPassword { get; init; }
    public string? ListenHost { get; init; }
    public int ListenPort { get; init; }
    public string? Flow { get; init; }
    public string? RealityPrivateKey { get; init; }
    public string? RealityPublicKey { get; init; }
    public string? ShortId { get; init; }
    public string? ServerName { get; init; }
    public string? Destination { get; init; }
    public string? Fingerprint { get; init; }
    public Guid? RelayNodeId { get; init; }
    public int? RelayPort { get; init; }
    public bool? Ipv4Only { get; init; }
    public XrayRouteConfig?[]? Routes { get; init; }
}

/// <summary>AnyTLS: TLS with the Panel's certificate, users authenticated by password.</summary>
internal sealed class MihomoAnyTlsConfig : IMihomoServiceConfig
{
    public string? UpstreamProxyType { get; init; }
    public string? UpstreamProxyHost { get; init; }
    public int? UpstreamProxyPort { get; init; }
    public string? UpstreamProxyUsername { get; init; }
    public string? UpstreamProxyPassword { get; init; }
    public string? ListenHost { get; init; }
    public int ListenPort { get; init; }
    public Guid CertificateId { get; init; }
    public Guid? RelayNodeId { get; init; }
    public int? RelayPort { get; init; }
    public bool? Ipv4Only { get; init; }
    public XrayRouteConfig?[]? Routes { get; init; }
}

/// <summary>Shadowsocks 2022 with a server key; users' keys are derived from their credentials.</summary>
internal sealed class MihomoShadowsocksConfig : IMihomoServiceConfig
{
    public string? UpstreamProxyType { get; init; }
    public string? UpstreamProxyHost { get; init; }
    public int? UpstreamProxyPort { get; init; }
    public string? UpstreamProxyUsername { get; init; }
    public string? UpstreamProxyPassword { get; init; }
    public string? ListenHost { get; init; }
    public int ListenPort { get; init; }
    public string? Cipher { get; init; }
    public string? ServerKey { get; init; }
    public bool? Ipv4Only { get; init; }
    public XrayRouteConfig?[]? Routes { get; init; }
}
