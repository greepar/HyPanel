namespace HyPanel.Agent;

using System.Text.Json.Serialization;
using HyPanel.Shared.Contracts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentLocalState))]
[JsonSerializable(typeof(NodeDesiredState))]
[JsonSerializable(typeof(AgentCommandStoreState))]
[JsonSerializable(typeof(AgentUsageStoreState))]
[JsonSerializable(typeof(AgentCommandResult))]
[JsonSerializable(typeof(AgentCommandResult[]))]
[JsonSerializable(typeof(UsageBatch))]
[JsonSerializable(typeof(UsageBatch[]))]
internal sealed partial class AgentSyncJsonSerializerContext : JsonSerializerContext;
