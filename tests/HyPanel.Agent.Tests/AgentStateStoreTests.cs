using HyPanel.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class AgentStateStoreTests
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
    public async Task SaveAndLoadAsync_PreservesAppliedRevision()
    {
        var options = new AgentEnrollmentOptions(null, null, dataDirectory);
        var store = new AgentStateStore(options);

        await store.SaveAsync(new AgentLocalState(42), CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(42L, loaded.AppliedRevision);
    }

    [TestMethod]
    public async Task LoadAsync_CorruptDesiredState_FailsWithoutReplacingFile()
    {
        Directory.CreateDirectory(dataDirectory);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "state.json"), "{not-json");
        var store = new AgentStateStore(new AgentEnrollmentOptions(null, null, dataDirectory));

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.LoadAsync(CancellationToken.None));

        StringAssert.Contains(exception.Message, "corrupt");
        Assert.IsTrue(File.Exists(Path.Combine(dataDirectory, "state.json")));
    }
}
