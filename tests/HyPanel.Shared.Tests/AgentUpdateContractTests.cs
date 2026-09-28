using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Shared.Tests;

[TestClass]
public sealed class AgentUpdateContractTests
{
    [TestMethod]
    public void AgentSyncResponse_RoundTrip_PreservesDedicatedUpdateOffer()
    {
        var update = new AgentUpdateDescriptor(Guid.NewGuid(), "1.3.0-beta.1", "linux-musl-arm64",
            "hypanel-agent-1.3.0-beta.1-linux-musl-arm64.tar.gz", new string('a', 64), 1234);
        var response = new AgentSyncResponse(4, null, [], [], 8, update);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(response,
            HyPanelJsonSerializerContext.Default.AgentSyncResponse);
        var roundTrip = JsonSerializer.Deserialize(bytes, HyPanelJsonSerializerContext.Default.AgentSyncResponse);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(update, roundTrip.AgentUpdate);
        Assert.AreEqual(0, roundTrip.Commands.Count);
    }

    [TestMethod]
    public void AgentSyncRequest_RoundTrip_PreservesUpdateReportOutsideCommands()
    {
        var updateId = Guid.NewGuid();
        var report = new AgentUpdateReport(updateId, AgentUpdateStatus.Verifying, "1.3.0", "linux-x64",
            DateTimeOffset.Parse("2026-09-16T00:00:00Z"), "1.2.0", null);
        var request = new AgentSyncRequest("1.3.0", "linux-x64", 2,
            new NodeMetrics(DateTimeOffset.Parse("2026-09-16T00:00:00Z"), 1, 2, 3, 2, 4, 3, 5, 6), [], [], [], report);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request,
            HyPanelJsonSerializerContext.Default.AgentSyncRequest);
        var roundTrip = JsonSerializer.Deserialize(bytes, HyPanelJsonSerializerContext.Default.AgentSyncRequest);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(updateId, roundTrip.AgentUpdate!.UpdateId);
        Assert.AreEqual(AgentUpdateStatus.Verifying, roundTrip.AgentUpdate.Status);
        Assert.AreEqual(0, roundTrip.CommandResults.Count);
    }

    [TestMethod]
    public void AgentSyncRequest_RoundTrip_PreservesAvxCapabilityAndAppliedArtifact()
    {
        var artifact = new BackendArtifact("hysteria2", "2.12.2", "linux-x64",
            "hysteria2-2.12.2-linux-x64-avx", new string('a', 64), 123, true);
        var request = new AgentSyncRequest("1.3.0", "linux-x64", 2,
            new NodeMetrics(DateTimeOffset.UtcNow, 1, 2, 3, 2, 4, 3, 5, 6), [], [], [],
            SupportsAvx: true, AppliedBackendArtifacts: [artifact]);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(request,
            HyPanelJsonSerializerContext.Default.AgentSyncRequest);
        var roundTrip = JsonSerializer.Deserialize(bytes, HyPanelJsonSerializerContext.Default.AgentSyncRequest);

        Assert.IsNotNull(roundTrip);
        Assert.IsTrue(roundTrip.SupportsAvx);
        Assert.AreEqual(artifact, roundTrip.AppliedBackendArtifacts!.Single());
    }
}
