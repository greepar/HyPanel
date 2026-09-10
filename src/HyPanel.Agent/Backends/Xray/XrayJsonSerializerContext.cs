namespace HyPanel.Agent.Backends.Xray;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(XrayConfig))]
internal sealed partial class XrayJsonSerializerContext : JsonSerializerContext;

internal sealed class XrayConfig
{
    public string? ListenHost { get; init; }

    public int ListenPort { get; init; }

    public string? ClientId { get; init; }

    public string? ClientEmail { get; init; }

    public string? Flow { get; init; }

    public string? RealityPrivateKey { get; init; }

    public string? RealityPublicKey { get; init; }

    public string? ShortId { get; init; }

    public string? ServerName { get; init; }

    public string? Destination { get; init; }

    public string? Fingerprint { get; init; }
}