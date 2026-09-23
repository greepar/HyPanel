namespace HyPanel.Agent.Backends.Hysteria2;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(Hysteria2Config))]
internal sealed partial class Hysteria2JsonSerializerContext : JsonSerializerContext;

/// <summary>Response of the Hysteria2 trafficStats API: <c>GET /traffic</c> keyed by auth identity.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, Hysteria2TrafficCounter>))]
internal sealed partial class Hysteria2TrafficJsonSerializerContext : JsonSerializerContext;

internal sealed class Hysteria2TrafficCounter
{
    /// <summary>Bytes the client transmitted (the user's upload); verified against a live server.</summary>
    public long Tx { get; init; }

    /// <summary>Bytes the client received (the user's download); verified against a live server.</summary>
    public long Rx { get; init; }
}

internal sealed class Hysteria2Config
{
    public string? ListenHost { get; init; }

    public int ListenPort { get; init; }

    public Guid CertificateId { get; init; }

    public string? CertificatePath { get; init; }

    public string? PrivateKeyPath { get; init; }

    public string? AuthPassword { get; init; }

    public string? MasqueradeUrl { get; init; }

    public string? ObfsPassword { get; init; }

    public int UpMbps { get; init; }

    public int DownMbps { get; init; }

    /// <summary>Optional port-hopping ports redirected to <see cref="ListenPort"/>, e.g. <c>20000-30000</c>.</summary>
    public string? PortHopping { get; init; }
}
