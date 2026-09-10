using System.Text.Json;
using HyPanel.Agent;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class AgentUsageStateStoreTests
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
    public async Task EnqueueAndLoadAsync_PersistsPendingBatch()
    {
        var batch = CreateBatch(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        var store = CreateStore();
        await store.LoadAsync(CancellationToken.None);
        await store.EnqueueAsync(batch, CancellationToken.None);

        var loaded = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.AreEqual(1, loaded.PendingBatches.Count);
        var loadedBatch = loaded.PendingBatches[0];
        Assert.AreEqual(batch.BatchId, loadedBatch.BatchId);
        Assert.AreEqual(batch.ObservedAt, loadedBatch.ObservedAt);
        CollectionAssert.AreEqual(batch.Records.ToArray(), loadedBatch.Records.ToArray());
    }

    [TestMethod]
    public async Task LoadAsync_MissingFile_ReturnsEmptyState()
    {
        var state = await CreateStore().LoadAsync(CancellationToken.None);

        Assert.AreEqual(0, state.PendingBatches.Count);
    }

    [TestMethod]
    public async Task EnqueueAsync_RejectsDuplicateAndOverCapacityBatches()
    {
        var store = CreateStore();
        await store.LoadAsync(CancellationToken.None);
        var batch = CreateBatch(Guid.NewGuid());
        await store.EnqueueAsync(batch, CancellationToken.None);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.EnqueueAsync(batch, CancellationToken.None));

        for (var index = 1; index < AgentUsageStateStore.MaxPendingBatches; index++)
        {
            await store.EnqueueAsync(CreateBatch(Guid.NewGuid()), CancellationToken.None);
        }

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.EnqueueAsync(CreateBatch(Guid.NewGuid()), CancellationToken.None));
    }

    [TestMethod]
    public async Task LoadAsync_RejectsBatchWithTooManyRecords()
    {
        Directory.CreateDirectory(dataDirectory);
        var batch = new UsageBatch(Guid.NewGuid(), DateTimeOffset.UtcNow,
            Enumerable.Range(0, AgentUsageStateStore.MaxRecordsPerBatch + 1)
                .Select(_ => new UserUsageDelta(Guid.NewGuid(), Guid.NewGuid(), 1, 2)).ToArray());
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, "usage-state.json"),
            JsonSerializer.Serialize(new AgentUsageStoreState([batch])));

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            CreateStore().LoadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task EnqueueAsync_RejectsInvalidIdentityCountersAndNonUtcTimestamp()
    {
        var store = CreateStore();
        await store.LoadAsync(CancellationToken.None);
        var valid = CreateBatch(Guid.NewGuid());

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.EnqueueAsync(valid with { BatchId = Guid.Empty }, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.EnqueueAsync(valid with
            {
                ObservedAt = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(1))
            }, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.EnqueueAsync(valid with
            {
                Records = [valid.Records[0] with { UserId = Guid.Empty }]
            }, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            store.EnqueueAsync(valid with
            {
                Records = [valid.Records[0] with { DownloadBytes = -1 }]
            }, CancellationToken.None));
        Assert.AreEqual(0, store.GetPendingBatchesSnapshot().Count);
    }

    [TestMethod]
    public async Task AcknowledgeAsync_RemovesOnlySentAcceptedBatches()
    {
        var store = CreateStore();
        await store.LoadAsync(CancellationToken.None);
        var sent = CreateBatch(Guid.NewGuid());
        var concurrent = CreateBatch(Guid.NewGuid());
        await store.EnqueueAsync(sent, CancellationToken.None);
        var snapshot = store.GetPendingBatchesSnapshot();
        await store.EnqueueAsync(concurrent, CancellationToken.None);

        await store.AcknowledgeAsync(snapshot, [sent.BatchId], CancellationToken.None);

        CollectionAssert.AreEqual(new[] { concurrent.BatchId },
            store.GetPendingBatchesSnapshot().Select(batch => batch.BatchId).ToArray());
    }

    [TestMethod]
    public async Task AcknowledgeAsync_RejectsUnknownAcknowledgement()
    {
        var store = CreateStore();
        await store.LoadAsync(CancellationToken.None);
        await store.EnqueueAsync(CreateBatch(Guid.NewGuid()), CancellationToken.None);

        await Assert.ThrowsExceptionAsync<JsonException>(() =>
            store.AcknowledgeAsync(store.GetPendingBatchesSnapshot(), [Guid.NewGuid()], CancellationToken.None));
    }

    private AgentUsageStateStore CreateStore() =>
        new(new AgentEnrollmentOptions(null, null, dataDirectory));

    private static UsageBatch CreateBatch(Guid batchId) =>
        new(batchId, DateTimeOffset.Parse("2026-09-10T12:34:56+00:00"),
        [
            new UserUsageDelta(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("33333333-3333-3333-3333-333333333333"), 100, 200)
        ]);
}