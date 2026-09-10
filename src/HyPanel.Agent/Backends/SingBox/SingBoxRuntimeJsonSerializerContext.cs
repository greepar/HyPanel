namespace HyPanel.Agent.Backends.SingBox;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SingBoxRuntimeConfig))]
internal sealed partial class SingBoxRuntimeJsonSerializerContext : JsonSerializerContext;

internal sealed class SingBoxRuntimeConfig
{
    public required SingBoxLog Log { get; init; }

    public required SingBoxInbound[] Inbounds { get; init; }

    public required SingBoxOutbound[] Outbounds { get; init; }
}

internal sealed class SingBoxLog
{
    public required string Level { get; init; }
}

internal sealed class SingBoxInbound
{
    public required string Type { get; init; }

    public required string Tag { get; init; }

    public required string Listen { get; init; }

    public required int ListenPort { get; init; }

    public required string Method { get; init; }

    public required string Password { get; init; }
}

internal sealed class SingBoxOutbound
{
    public required string Type { get; init; }

    public required string Tag { get; init; }
}