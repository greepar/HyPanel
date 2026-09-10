namespace HyPanel.Agent.Backends.SingBox;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    AllowDuplicateProperties = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(SingBoxConfig))]
internal sealed partial class SingBoxJsonSerializerContext : JsonSerializerContext;

internal sealed class SingBoxConfig
{
    public string? ListenHost { get; init; }

    public int ListenPort { get; init; }

    public string? Method { get; init; }

    public string? Password { get; init; }

    public bool Udp { get; init; }
}