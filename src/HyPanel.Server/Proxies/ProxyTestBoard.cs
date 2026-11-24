using System.Collections.Concurrent;
using HyPanel.Shared.Contracts;

namespace HyPanel.Server.Proxies;

/// <summary>
/// Proxy tests the admin started and what the nodes reported. A test goes to its node on the next sync and stays listed
/// until the Agent reports; results are kept a short while for the browser to pick up. In memory only: a test lasts
/// seconds, and holding proxy passwords on disk for it would be worse than losing it on a restart.
/// </summary>
internal sealed class ProxyTestBoard(TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, Test> tests = new();

    private sealed record Test(Guid NodeId, ProxyTestRequest Request, DateTimeOffset ExpiresAt, ProxyTestReport? Report);

    public Guid Post(Guid nodeId, string type, string host, int port, string? username, string? password)
    {
        Prune();
        var id = Guid.NewGuid();
        tests[id] = new Test(nodeId, new ProxyTestRequest(id, type, host, port, username, password),
            timeProvider.GetUtcNow() + Lifetime, null);
        return id;
    }

    /// <summary>The tests a node still has to run (the full list; a test no longer listed is withdrawn).</summary>
    public IReadOnlyList<ProxyTestRequest> For(Guid nodeId)
    {
        var now = timeProvider.GetUtcNow();
        return [.. tests.Values.Where(test => test.NodeId == nodeId && test.Report is null && test.ExpiresAt > now)
            .Select(test => test.Request)];
    }

    public void Record(Guid nodeId, IReadOnlyList<ProxyTestReport>? reports)
    {
        foreach (var report in reports ?? [])
            if (tests.TryGetValue(report.TestId, out var test) && test.NodeId == nodeId && test.Report is null)
                tests[report.TestId] = test with { Report = report };
    }

    /// <summary>
    /// The test as the admin sees it: null when unknown or expired, a null report while the node has not answered.
    /// </summary>
    public (bool Found, ProxyTestReport? Report) Get(Guid nodeId, Guid testId) =>
        tests.TryGetValue(testId, out var test) && test.NodeId == nodeId && test.ExpiresAt > timeProvider.GetUtcNow()
            ? (true, test.Report)
            : (false, null);

    private void Prune()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var (id, test) in tests)
            if (test.ExpiresAt <= now) tests.TryRemove(id, out _);
    }
}
