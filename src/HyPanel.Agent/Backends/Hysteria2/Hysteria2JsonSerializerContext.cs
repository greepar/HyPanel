namespace HyPanel.Agent.Backends.Hysteria2;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(Hysteria2Config))]
internal sealed partial class Hysteria2JsonSerializerContext : JsonSerializerContext;

internal sealed class Hysteria2Config
{
    public string? ListenHost { get; init; }

    public int ListenPort { get; init; }

    public string? CertificatePath { get; init; }

    public string? PrivateKeyPath { get; init; }

    public string? AuthPassword { get; init; }

    public string? MasqueradeUrl { get; init; }

    public string? ObfsPassword { get; init; }

    public int UpMbps { get; init; }

    public int DownMbps { get; init; }
}
