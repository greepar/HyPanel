namespace HyPanel.Agent.Backends.Xray;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(XrayRuntimeConfig))]
internal sealed partial class XrayRuntimeJsonSerializerContext : JsonSerializerContext;

internal sealed class XrayRuntimeConfig
{
    public required XrayLog Log { get; init; }
    public required XrayInbound[] Inbounds { get; init; }
    public required XrayOutbound[] Outbounds { get; init; }
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
}