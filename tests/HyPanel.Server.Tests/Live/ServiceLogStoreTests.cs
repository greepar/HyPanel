using HyPanel.Server.Live;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Live;

[TestClass]
public sealed class ServiceLogStoreTests
{
    private static readonly Guid Agent = Guid.NewGuid();
    private static readonly Guid Service = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [TestMethod]
    public async Task Append_ResentLines_AreStoredOnce()
    {
        var store = new ServiceLogStore();
        var stream = Guid.NewGuid();

        store.Append(Agent, stream, [new ServiceLogChunk(Service, 1, ["a", "b"])], Now);
        var cursors = store.Append(Agent, stream, [new ServiceLogChunk(Service, 2, ["b", "c"])], Now);

        var page = await store.ReadAsync(Service, 0, 100, TimeSpan.Zero, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "a", "b", "c" }, page.Lines.Select(line => line.Text).ToArray());
        Assert.AreEqual(3, cursors.Single().Sequence);
    }

    [TestMethod]
    public async Task Append_NewStream_ContinuesPanelNumbering()
    {
        var store = new ServiceLogStore();
        store.Append(Agent, Guid.NewGuid(), [new ServiceLogChunk(Service, 1, ["old"])], Now);

        // The Agent restarted: its numbers start over, the Panel's keep rising.
        store.Append(Agent, Guid.NewGuid(), [new ServiceLogChunk(Service, 1, ["new"])], Now);

        var page = await store.ReadAsync(Service, 1, 100, TimeSpan.Zero, CancellationToken.None);
        Assert.AreEqual("new", page.Lines.Single().Text);
        Assert.AreEqual(2, page.Lines.Single().Number);
    }

    [TestMethod]
    public void Cursors_UnknownStream_AreEmptySoTheAgentResends()
    {
        var store = new ServiceLogStore();
        var stream = Guid.NewGuid();
        store.Append(Agent, stream, [new ServiceLogChunk(Service, 1, ["a"])], Now);

        Assert.AreEqual(1, store.Cursors(Agent, stream).Count);
        Assert.AreEqual(0, store.Cursors(Agent, Guid.NewGuid()).Count);
    }

    [TestMethod]
    public async Task ReadAsync_Waiting_ReturnsWhenLinesArrive()
    {
        var store = new ServiceLogStore();
        var stream = Guid.NewGuid();

        var reading = store.ReadAsync(Service, 0, 100, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.IsFalse(reading.IsCompleted);
        store.Append(Agent, stream, [new ServiceLogChunk(Service, 1, ["hello"])], Now);

        var page = await reading.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("hello", page.Lines.Single().Text);
    }

    [TestMethod]
    public async Task ReadAsync_CursorAheadOfStore_AsksForReset()
    {
        var store = new ServiceLogStore();

        var page = await store.ReadAsync(Service, 50, 100, TimeSpan.Zero, CancellationToken.None);

        Assert.IsTrue(page.Reset);
        Assert.AreEqual(0, page.Cursor);
    }

    [TestMethod]
    public async Task Append_KeepsOnlyTheNewestLines()
    {
        var store = new ServiceLogStore();
        var lines = Enumerable.Range(1, ServiceLogStore.MaximumLinesPerService + 10).Select(i => $"l{i}").ToArray();
        store.Append(Agent, Guid.NewGuid(), [new ServiceLogChunk(Service, 1, lines[..1000]), ], Now);
        store.Append(Agent, Guid.NewGuid(), [new ServiceLogChunk(Service, 1, lines[1000..])], Now);

        var page = await store.ReadAsync(Service, 0, 5000, TimeSpan.Zero, CancellationToken.None);

        Assert.AreEqual(ServiceLogStore.MaximumLinesPerService, page.Lines.Count);
        Assert.AreEqual(lines[^1], page.Lines[^1].Text);
    }
}
