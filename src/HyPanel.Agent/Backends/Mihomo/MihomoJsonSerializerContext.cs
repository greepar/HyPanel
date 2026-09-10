namespace HyPanel.Agent.Backends.Mihomo;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(MihomoConfig))]
internal sealed partial class MihomoJsonSerializerContext : JsonSerializerContext;

internal sealed class MihomoConfig
{
    public string? ListenHost { get; init; }

    public int ListenPort { get; init; }

    public string? Method { get; init; }

    public string? Password { get; init; }

    public bool Udp { get; init; }
}