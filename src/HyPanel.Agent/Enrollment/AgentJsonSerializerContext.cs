using System.Text.Json.Serialization;

namespace HyPanel.Agent;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentCredentials))]
internal sealed partial class AgentJsonSerializerContext : JsonSerializerContext;
