using System.Collections.Concurrent;
using HyPanel.Shared.Contracts;

namespace HyPanel.Server.Acme;

/// <summary>
/// The <c>http-01</c> / <c>tls-alpn-01</c> challenges nodes are proving right now, and what their Agents reported.
/// The issuer posts a challenge for the nodes whose services use the certificate; each node's sync hands it out and
/// brings back whether the Agent is listening. Kept in memory: an issuance does not survive a Panel restart anyway.
/// </summary>
internal sealed class AcmeChallengeBoard
{
    private readonly ConcurrentDictionary<string, Posting> postings = new(StringComparer.Ordinal);

    private sealed record Posting(AcmeChallengeAssignment Challenge, IReadOnlySet<Guid> Nodes,
        ConcurrentDictionary<Guid, AcmeChallengeReport> Reports);

    public void Post(AcmeChallengeAssignment challenge, IReadOnlySet<Guid> nodes) =>
        postings[challenge.Token] = new Posting(challenge, nodes, new ConcurrentDictionary<Guid, AcmeChallengeReport>());

    public void Withdraw(string token) => postings.TryRemove(token, out _);

    /// <summary>Everything a node should answer (the full list; an empty list withdraws earlier ones).</summary>
    public IReadOnlyList<AcmeChallengeAssignment> For(Guid nodeId) =>
        [.. postings.Values.Where(posting => posting.Nodes.Contains(nodeId)).Select(posting => posting.Challenge)];

    public void Record(Guid nodeId, IReadOnlyList<AcmeChallengeReport>? reports)
    {
        foreach (var report in reports ?? [])
            if (postings.TryGetValue(report.Token, out var posting) && posting.Nodes.Contains(nodeId))
                posting.Reports[nodeId] = report;
    }

    /// <summary>What each node reported for the challenge so far (nodes that did not sync yet are missing).</summary>
    public IReadOnlyDictionary<Guid, AcmeChallengeReport> Reports(string token) =>
        postings.TryGetValue(token, out var posting) ? posting.Reports : new Dictionary<Guid, AcmeChallengeReport>();
}
