using HyPanel.Agent;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class AgentCommandStateStoreTests
{
    private string dataDirectory = null!;

    [TestInitialize]
    public void Initialize()
    {
        dataDirectory = Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaveAndLoadAsync_PreservesPendingResultAndRecentCompletedIds()
    {
        var options = new AgentEnrollmentOptions(null, null, dataDirectory);
        var store = new AgentCommandStateStore(options);
        var commandId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var completedAt = DateTimeOffset.Parse("2026-09-09T12:34:56+00:00");
        var state = new AgentCommandStoreState(
            [new AgentCommandResult(commandId, AgentCommandStatus.Failed, completedAt.AddSeconds(-2), completedAt, "failed", "details")],
            [commandId, Guid.Parse("22222222-2222-2222-2222-222222222222")]);

        await store.SaveAsync(state, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(1, loaded.PendingResults.Count);
        var result = loaded.PendingResults[0];
        Assert.AreEqual(commandId, result.CommandId);
        Assert.AreEqual(AgentCommandStatus.Failed, result.Status);
        Assert.AreEqual(completedAt.AddSeconds(-2), result.StartedAt);
        Assert.AreEqual(completedAt, result.CompletedAt);
        Assert.AreEqual("failed", result.ErrorCode);
        Assert.AreEqual("details", result.ErrorMessage);
        CollectionAssert.AreEqual(state.RecentCompletedIds.ToArray(), loaded.RecentCompletedIds.ToArray());
    }
}
