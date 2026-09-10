namespace HyPanel.Agent;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AgentCredentials))]
internal sealed partial class AgentJsonSerializerContext : JsonSerializerContext;
