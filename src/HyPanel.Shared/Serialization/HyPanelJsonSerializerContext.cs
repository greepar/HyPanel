namespace HyPanel.Shared.Serialization;

using System.Text.Json.Serialization;
using HyPanel.Shared.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AgentEnrollmentRequest))]
[JsonSerializable(typeof(AgentEnrollmentResponse))]
[JsonSerializable(typeof(AgentSyncRequest))]
[JsonSerializable(typeof(AgentSyncResponse))]
[JsonSerializable(typeof(NodeDesiredState))]
[JsonSerializable(typeof(NodeMetrics))]
[JsonSerializable(typeof(ServiceDesiredState))]
[JsonSerializable(typeof(ServiceDesiredState[]))]
[JsonSerializable(typeof(BackendUser))]
[JsonSerializable(typeof(BackendUser[]))]
[JsonSerializable(typeof(TlsCertificateAsset))]
[JsonSerializable(typeof(BackendArtifact))]
[JsonSerializable(typeof(BackendArtifact[]))]
[JsonSerializable(typeof(ServiceRuntimeState))]
[JsonSerializable(typeof(ServiceRuntimeState[]))]
[JsonSerializable(typeof(BackendTrafficSnapshot))]
[JsonSerializable(typeof(UsageBatch))]
[JsonSerializable(typeof(UsageBatch[]))]
[JsonSerializable(typeof(UserUsageDelta))]
[JsonSerializable(typeof(UserUsageDelta[]))]
[JsonSerializable(typeof(Guid[]))]
[JsonSerializable(typeof(AgentCommand))]
[JsonSerializable(typeof(AgentCommand[]))]
[JsonSerializable(typeof(AgentCommandResult))]
[JsonSerializable(typeof(AgentCommandResult[]))]
[JsonSerializable(typeof(ApiError))]
[JsonSerializable(typeof(AgentReleaseManifest))]
[JsonSerializable(typeof(AgentReleaseAsset))]
[JsonSerializable(typeof(AgentReleaseAsset[]))]
[JsonSerializable(typeof(AgentUpdateDescriptor))]
[JsonSerializable(typeof(AgentUpdateReport))]
public sealed partial class HyPanelJsonSerializerContext : JsonSerializerContext;
