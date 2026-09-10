using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Shared.Tests.Serialization;

[TestClass]
public sealed class HyPanelJsonSerializerContextTests
{
    [TestMethod]
    public void AgentEnrollmentRequest_RoundTripsWithCamelCaseProperties()
    {
        var request = new AgentEnrollmentRequest(
            "enrollment-token",
            "1.2.3",
            "linux-x64",
            new AgentMachineInfo("node-a"));

        var json = JsonSerializer.Serialize(request, HyPanelJsonSerializerContext.Default.AgentEnrollmentRequest);
        var result = JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.AgentEnrollmentRequest);

        Assert.IsNotNull(result);
        StringAssert.Contains(json, "\"agentVersion\"");
        StringAssert.Contains(json, "\"machine\"");
        Assert.IsFalse(json.Contains("\"AgentVersion\"", StringComparison.Ordinal));
        Assert.AreEqual(request.Token, result.Token);
        Assert.AreEqual(request.AgentVersion, result.AgentVersion);
        Assert.AreEqual(request.Platform, result.Platform);
        Assert.AreEqual(request.Machine.Hostname, result.Machine.Hostname);
    }

    [TestMethod]
    public void AgentEnrollmentResponse_RoundTripsWithCamelCaseProperties()
    {
        var response = new AgentEnrollmentResponse(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "agent-secret",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            30);

        var json = JsonSerializer.Serialize(response, HyPanelJsonSerializerContext.Default.AgentEnrollmentResponse);
        var result = JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.AgentEnrollmentResponse);

        Assert.IsNotNull(result);
        StringAssert.Contains(json, "\"agentId\"");
        StringAssert.Contains(json, "\"syncIntervalSeconds\"");
        Assert.AreEqual(response.AgentId, result.AgentId);
        Assert.AreEqual(response.AgentSecret, result.AgentSecret);
        Assert.AreEqual(response.NodeId, result.NodeId);
        Assert.AreEqual(response.SyncIntervalSeconds, result.SyncIntervalSeconds);
    }

    [TestMethod]
    public void AgentSyncRequest_RoundTripsWithCommandResultStringEnum()
    {
        var observedAt = DateTimeOffset.Parse("2026-09-09T12:34:56+00:00");
        var request = new AgentSyncRequest(
            "2.0.0",
            "windows-x64",
            41,
            new NodeMetrics(observedAt, 1234, 12.5, 1000, 600, 2000, 1500, 700, 800),
            [],
            [
                new UsageBatch(
                    Guid.Parse("77777777-7777-7777-7777-777777777777"),
                    observedAt,
                    [
                        new UserUsageDelta(
                            Guid.Parse("88888888-8888-8888-8888-888888888888"),
                            Guid.Parse("99999999-9999-9999-9999-999999999999"),
                            123,
                            456)
                    ])
            ],
            [
                new AgentCommandResult(
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    AgentCommandStatus.Succeeded,
                    observedAt,
                    observedAt.AddSeconds(2),
                    null,
                    null)
            ]);

        var json = JsonSerializer.Serialize(request, HyPanelJsonSerializerContext.Default.AgentSyncRequest);
        var result = JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.AgentSyncRequest);

        Assert.IsNotNull(result);
        StringAssert.Contains(json, "\"appliedRevision\"");
        StringAssert.Contains(json, "\"status\":\"Succeeded\"");
        Assert.AreEqual(request.AgentVersion, result.AgentVersion);
        Assert.AreEqual(request.Platform, result.Platform);
        Assert.AreEqual(request.AppliedRevision, result.AppliedRevision);
        Assert.AreEqual(request.Metrics.ObservedAt, result.Metrics.ObservedAt);
        Assert.AreEqual(request.Metrics.UptimeSeconds, result.Metrics.UptimeSeconds);
        Assert.AreEqual(request.Metrics.CpuUsagePercent, result.Metrics.CpuUsagePercent);
        Assert.AreEqual(request.Metrics.MemoryTotalBytes, result.Metrics.MemoryTotalBytes);
        Assert.AreEqual(request.Metrics.MemoryAvailableBytes, result.Metrics.MemoryAvailableBytes);
        Assert.AreEqual(request.Metrics.DiskTotalBytes, result.Metrics.DiskTotalBytes);
        Assert.AreEqual(request.Metrics.DiskAvailableBytes, result.Metrics.DiskAvailableBytes);
        Assert.AreEqual(request.Metrics.NetworkUploadBytes, result.Metrics.NetworkUploadBytes);
        Assert.AreEqual(request.Metrics.NetworkDownloadBytes, result.Metrics.NetworkDownloadBytes);
        Assert.AreEqual(1, result.UsageBatches.Count);
        Assert.AreEqual(request.UsageBatches[0].BatchId, result.UsageBatches[0].BatchId);
        Assert.AreEqual(request.UsageBatches[0].ObservedAt, result.UsageBatches[0].ObservedAt);
        Assert.AreEqual(request.UsageBatches[0].Records[0].UserId, result.UsageBatches[0].Records[0].UserId);
        Assert.AreEqual(request.UsageBatches[0].Records[0].ServiceId, result.UsageBatches[0].Records[0].ServiceId);
        Assert.AreEqual(123L, result.UsageBatches[0].Records[0].UploadBytes);
        Assert.AreEqual(456L, result.UsageBatches[0].Records[0].DownloadBytes);
        Assert.AreEqual(request.CommandResults.Count, result.CommandResults.Count);
        Assert.AreEqual(request.CommandResults[0].CommandId, result.CommandResults[0].CommandId);
        Assert.AreEqual(request.CommandResults[0].Status, result.CommandResults[0].Status);
        Assert.AreEqual(request.CommandResults[0].StartedAt, result.CommandResults[0].StartedAt);
        Assert.AreEqual(request.CommandResults[0].CompletedAt, result.CommandResults[0].CompletedAt);
        Assert.AreEqual(request.CommandResults[0].ErrorCode, result.CommandResults[0].ErrorCode);
        Assert.AreEqual(request.CommandResults[0].ErrorMessage, result.CommandResults[0].ErrorMessage);
    }

    [TestMethod]
    public void AgentSyncResponse_RoundTripsWithCamelCaseProperties()
    {
        var commandId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var response = new AgentSyncResponse(
            42,
            new NodeDesiredState(42, [], []),
            [
                new AgentCommand(commandId, AgentCommandType.RunHealthCheck,
                    DateTimeOffset.Parse("2026-09-09T12:00:00+00:00"), null)
            ],
            [Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")],
            15);

        var json = JsonSerializer.Serialize(response, HyPanelJsonSerializerContext.Default.AgentSyncResponse);
        var result = JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.AgentSyncResponse);

        Assert.IsNotNull(result);
        StringAssert.Contains(json, "\"desiredRevision\"");
        StringAssert.Contains(json, "\"type\":\"RunHealthCheck\"");
        Assert.AreEqual(response.DesiredRevision, result.DesiredRevision);
        var desiredState = result.DesiredState;
        Assert.IsNotNull(desiredState);
        Assert.AreEqual(42L, desiredState.Revision);
        Assert.AreEqual(response.Commands.Count, result.Commands.Count);
        Assert.AreEqual(response.Commands[0].CommandId, result.Commands[0].CommandId);
        Assert.AreEqual(response.Commands[0].Type, result.Commands[0].Type);
        Assert.AreEqual(response.Commands[0].CreatedAt, result.Commands[0].CreatedAt);
        Assert.AreEqual(response.Commands[0].ExpiresAt, result.Commands[0].ExpiresAt);
        CollectionAssert.AreEqual(response.AcceptedUsageBatchIds.ToArray(), result.AcceptedUsageBatchIds.ToArray());
        Assert.AreEqual(response.SyncIntervalSeconds, result.SyncIntervalSeconds);
    }

    [TestMethod]
    public void RunHealthCheckCommand_RoundTripsWithStringEnum()
    {
        var command = new AgentCommand(
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            AgentCommandType.RunHealthCheck,
            DateTimeOffset.Parse("2026-09-09T13:00:00+00:00"),
            DateTimeOffset.Parse("2026-09-09T13:05:00+00:00"));

        var json = JsonSerializer.Serialize(command, HyPanelJsonSerializerContext.Default.AgentCommand);
        var result = JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.AgentCommand);

        Assert.IsNotNull(result);
        StringAssert.Contains(json, "\"type\":\"RunHealthCheck\"");
        Assert.AreEqual(command.CommandId, result.CommandId);
        Assert.AreEqual(command.Type, result.Type);
        Assert.AreEqual(command.CreatedAt, result.CreatedAt);
        Assert.AreEqual(command.ExpiresAt, result.ExpiresAt);
    }

    [TestMethod]
    public void RunHealthCheckCommandResult_RoundTripsWithStringEnum()
    {
        var result = new AgentCommandResult(
            Guid.Parse("66666666-6666-6666-6666-666666666666"),
            AgentCommandStatus.Failed,
            DateTimeOffset.Parse("2026-09-09T14:00:00+00:00"),
            DateTimeOffset.Parse("2026-09-09T14:00:03+00:00"),
            "health_check_failed",
            "Backend did not respond");

        var json = JsonSerializer.Serialize(result, HyPanelJsonSerializerContext.Default.AgentCommandResult);
        var roundTripped = JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.AgentCommandResult);

        Assert.IsNotNull(roundTripped);
        StringAssert.Contains(json, "\"status\":\"Failed\"");
        Assert.AreEqual(result.CommandId, roundTripped.CommandId);
        Assert.AreEqual(result.Status, roundTripped.Status);
        Assert.AreEqual(result.StartedAt, roundTripped.StartedAt);
        Assert.AreEqual(result.CompletedAt, roundTripped.CompletedAt);
        Assert.AreEqual(result.ErrorCode, roundTripped.ErrorCode);
        Assert.AreEqual(result.ErrorMessage, roundTripped.ErrorMessage);
    }
}