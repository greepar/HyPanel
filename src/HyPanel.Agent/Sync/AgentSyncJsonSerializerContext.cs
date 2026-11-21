using System.Text.Json.Serialization;
using HyPanel.Agent.Updates;
using HyPanel.Shared.Contracts;

namespace HyPanel.Agent;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentLocalState))]
[JsonSerializable(typeof(NodeDesiredState))]
[JsonSerializable(typeof(AgentCommandStoreState))]
[JsonSerializable(typeof(AgentUsageStoreState))]
[JsonSerializable(typeof(AgentCommandResult))]
[JsonSerializable(typeof(AgentCommandResult[]))]
[JsonSerializable(typeof(UsageBatch))]
[JsonSerializable(typeof(UsageBatch[]))]
[JsonSerializable(typeof(UserTrafficCounter))]
[JsonSerializable(typeof(UserTrafficCounter[]))]
[JsonSerializable(typeof(AgentUpdateLocalState))]
internal sealed partial class AgentSyncJsonSerializerContext : JsonSerializerContext;
