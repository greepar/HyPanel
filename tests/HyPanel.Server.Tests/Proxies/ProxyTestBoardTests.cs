using HyPanel.Server.Proxies;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Proxies;

[TestClass]
public sealed class ProxyTestBoardTests
{
    [TestMethod]
    public void Test_GoesToItsNodeUntilReportedAndExpires()
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-24T00:00:00Z"));
        var board = new ProxyTestBoard(time);
        var node = Guid.NewGuid();
        var other = Guid.NewGuid();
        var id = board.Post(node, "socks5", "p.example", 1080, "u", "p");

        Assert.AreEqual(id, board.For(node).Single().TestId);
        Assert.AreEqual(0, board.For(other).Count);
        Assert.AreEqual((true, (ProxyTestReport?)null), board.Get(node, id));

        var report = new ProxyTestReport(id, true, "198.51.100.7", "FR", "Paris", 120, null);
        board.Record(other, [report]);
        Assert.IsNull(board.Get(node, id).Report, "another node cannot answer the test");
        board.Record(node, [report]);
        Assert.AreEqual(report, board.Get(node, id).Report);
        Assert.AreEqual(0, board.For(node).Count, "an answered test is withdrawn");
        Assert.IsFalse(board.Get(other, id).Found);

        time.Advance(TimeSpan.FromMinutes(3));
        Assert.IsFalse(board.Get(node, id).Found);
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset now = utcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
