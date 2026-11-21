using System.Collections.Concurrent;

namespace HyPanel.Server.Live;

/// <summary>
/// A counter the admin UI long-polls so it reloads as soon as something it shows changes: an admin edit, or an Agent
/// reporting a different service status, applied revision or address. Plain metric ticks do not count; the UI still
/// refreshes those on its own timer.
/// </summary>
internal sealed class ChangeFeed
{
    private readonly Lock gate = new();
    private readonly ConcurrentDictionary<Guid, string> agentFingerprints = new();
    private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long version = 1;

    public long Version
    {
        get { lock (gate) return version; }
    }

    public void Bump()
    {
        TaskCompletionSource fired;
        lock (gate)
        {
            version++;
            fired = signal;
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fired.TrySetResult();
    }

    /// <summary>Bumps when the Agent's report differs from its previous one in a way the UI shows.</summary>
    public void ObserveAgent(Guid agentId, string fingerprint)
    {
        var previous = agentFingerprints.GetValueOrDefault(agentId);
        agentFingerprints[agentId] = fingerprint;
        if (previous != fingerprint) Bump();
    }

    /// <summary>Returns the current version once it is past <paramref name="since"/>, or after the wait runs out.</summary>
    public async Task<long> WaitAsync(long since, TimeSpan wait, CancellationToken cancellationToken)
    {
        Task pending;
        lock (gate)
        {
            if (version != since) return version;
            pending = signal.Task;
        }

        try
        {
            await pending.WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
        }

        return Version;
    }
}
