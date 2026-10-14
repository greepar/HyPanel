namespace HyPanel.Agent.Backends.Xray;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(XrayRuntimeConfig))]
[JsonSerializable(typeof(XrayShadowsocksRuntimeConfig))]
internal sealed partial class XrayRuntimeJsonSerializerContext : JsonSerializerContext;

internal sealed class XrayRuntimeConfig
{
    public required XrayLog Log { get; init; }
    public required XrayApi Api { get; init; }
    public required XrayStats Stats { get; init; }
    public required XrayPolicy Policy { get; init; }
    public required XrayInbound[] Inbounds { get; init; }
    public required XrayOutbound[] Outbounds { get; init; }
}

internal sealed class XrayShadowsocksRuntimeConfig
{
    public required XrayLog Log { get; init; }
    public required XrayApi Api { get; init; }
    public required XrayStats Stats { get; init; }
    public required XrayPolicy Policy { get; init; }
    public required XrayShadowsocksInbound[] Inbounds { get; init; }
    public required XrayOutbound[] Outbounds { get; init; }
}

internal sealed class XrayShadowsocksInbound
{
    public required string Listen { get; init; }
    public required int Port { get; init; }
    public required string Protocol { get; init; }
    public required XrayShadowsocksSettings Settings { get; init; }
}

internal sealed class XrayShadowsocksSettings
{
    public required string Method { get; init; }
    public required string Password { get; init; }
    public required string Network { get; init; }
    public required XrayShadowsocksClient[] Clients { get; init; }
}

internal sealed class XrayShadowsocksClient
{
    public required string Password { get; init; }
    public required string Email { get; init; }
    public required int Level { get; init; }
}

internal sealed class XrayApi
{
    public required string Tag { get; init; }
    public required string Listen { get; init; }
    public required string[] Services { get; init; }
}

internal sealed class XrayStats;

internal sealed class XrayPolicy
{
    public required Dictionary<string, XrayLevelPolicy> Levels { get; init; }
}

internal sealed class XrayLevelPolicy
{
    public required bool StatsUserUplink { get; init; }
    public required bool StatsUserDownlink { get; init; }
}

internal sealed class XrayLog
{
    public required string LogLevel { get; init; }
}

internal sealed class XrayInbound
{
    public required string Listen { get; init; }
    public required int Port { get; init; }
    public required string Protocol { get; init; }
    public required XrayInboundSettings Settings { get; init; }
    public required XrayStreamSettings StreamSettings { get; init; }
}

internal sealed class XrayInboundSettings
{
    public required XrayClient[] Clients { get; init; }
    public required string Decryption { get; init; }
}

internal sealed class XrayClient
{
    public required string Id { get; init; }
    public required string Email { get; init; }
    public required string Flow { get; init; }
    public required int Level { get; init; }
}

internal sealed class XrayStreamSettings
{
    public required string Network { get; init; }
    public required string Security { get; init; }
    public required XrayRealitySettings RealitySettings { get; init; }
}

internal sealed class XrayRealitySettings
{
    public required bool Show { get; init; }
    public required string Dest { get; init; }
    public required int Xver { get; init; }
    public required string[] ServerNames { get; init; }
    public required string PrivateKey { get; init; }
    public required string[] ShortIds { get; init; }
}

internal sealed class XrayOutbound
{
    public required string Protocol { get; init; }
    public required string Tag { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XrayProxyOutboundSettings? Settings { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XrayOutboundStream? StreamSettings { get; init; }

    /// <summary>
    /// Xray sends everything through the first outbound: the upstream proxy when one is configured (it fails closed,
    /// there is no fallback to a direct connection), otherwise a direct connection.
    /// </summary>
    public static XrayOutbound[] Create(HyPanel.Shared.Networking.UpstreamProxy? proxy)
    {
        var blocked = new XrayOutbound { Protocol = "blackhole", Tag = "blocked" };
        if (proxy is null) return [new XrayOutbound { Protocol = "freedom", Tag = "direct" }, blocked];
        var server = new XrayProxyServer
        {
            Address = proxy.Host,
            Port = proxy.Port,
            Users = proxy.Username is null ? null : [new XrayProxyUser { User = proxy.Username, Pass = proxy.Password ?? "" }]
        };
        return
        [
            new XrayOutbound
            {
                Protocol = proxy.Type == HyPanel.Shared.Networking.UpstreamProxy.Socks5 ? "socks" : "http",
                Tag = "upstream",
                Settings = new XrayProxyOutboundSettings { Servers = [server] },
                StreamSettings = proxy.Type == HyPanel.Shared.Networking.UpstreamProxy.Https
                    ? new XrayOutboundStream { Security = "tls", TlsSettings = new XrayTlsSettings { ServerName = proxy.Host } }
                    : null
            },
            blocked
        ];
    }
}

internal sealed class XrayProxyOutboundSettings
{
    public required XrayProxyServer[] Servers { get; init; }
}

internal sealed class XrayProxyServer
{
    public required string Address { get; init; }
    public required int Port { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XrayProxyUser[]? Users { get; init; }
}

internal sealed class XrayProxyUser
{
    public required string User { get; init; }
    public required string Pass { get; init; }
}

internal sealed class XrayOutboundStream
{
    public required string Security { get; init; }
    public required XrayTlsSettings TlsSettings { get; init; }
}

internal sealed class XrayTlsSettings
{
    public required string ServerName { get; init; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(XrayStatsQueryResult))]
internal sealed partial class XrayStatsJsonSerializerContext : JsonSerializerContext;

internal sealed class XrayStatsQueryResult
{
    public XrayStat[]? Stat { get; init; }
}

internal sealed class XrayStat
{
    public string? Name { get; init; }
    public long Value { get; init; }
}
