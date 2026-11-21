using HyPanel.Shared.Contracts;

namespace HyPanel.Server.Live;

/// <summary>
/// Keeps the most recent output lines of every service in memory, fed by Agents on each sync. Lines get a Panel-side
/// number that keeps rising across Agent restarts, so a browser can follow a service with "lines after N". After a
/// Panel restart the store is empty and each Agent sends its own buffer again (it learns this from the cursors).
/// </summary>
internal sealed class ServiceLogStore
{
    internal const int MaximumLinesPerService = 2000;
    internal const int MaximumLineCharacters = 4096;
    internal const int MaximumLinesPerChunk = 1024;

    private readonly Lock gate = new();
    private readonly Dictionary<Guid, ServiceLog> services = [];
    private readonly Dictionary<Guid, AgentStream> agents = [];

    /// <summary>
    /// Stores the new lines of one sync and returns the cursors the Agent should continue from. Lines the store
    /// already has (a resend after a lost response) are skipped.
    /// </summary>
    public IReadOnlyList<ServiceLogCursor> Append(Guid agentId, Guid streamId, IReadOnlyList<ServiceLogChunk> chunks,
        DateTimeOffset now)
    {
        List<TaskCompletionSource>? wake = null;
        lock (gate)
        {
            if (!agents.TryGetValue(agentId, out var stream) || stream.StreamId != streamId)
                agents[agentId] = stream = new AgentStream(streamId);
            foreach (var chunk in chunks)
            {
                if (chunk.ServiceId == Guid.Empty || chunk.FirstSequence < 1 || chunk.Lines is null) continue;
                stream.Cursors.TryGetValue(chunk.ServiceId, out var cursor);
                // A gap means the Agent's buffer rolled over before it could send; keep what arrived.
                var skip = Math.Max(0, cursor - chunk.FirstSequence + 1);
                if (skip >= chunk.Lines.Count) continue;
                if (!services.TryGetValue(chunk.ServiceId, out var log)) services[chunk.ServiceId] = log = new ServiceLog();
                for (var index = (int)skip; index < chunk.Lines.Count; index++)
                {
                    var text = chunk.Lines[index] ?? string.Empty;
                    if (text.Length > MaximumLineCharacters) text = text[..MaximumLineCharacters];
                    log.Add(text, now);
                }
                stream.Cursors[chunk.ServiceId] = chunk.FirstSequence + chunk.Lines.Count - 1;
                if (log.TakeWaiter() is { } waiter) (wake ??= []).Add(waiter);
            }
            var cursors = stream.Cursors.Select(pair => new ServiceLogCursor(pair.Key, pair.Value)).ToArray();
            if (wake is not null) foreach (var waiter in wake) waiter.TrySetResult();
            return cursors;
        }
    }

    /// <summary>The cursors held for an Agent's stream; empty when the stream is unknown, which asks for a resend.</summary>
    public IReadOnlyList<ServiceLogCursor> Cursors(Guid agentId, Guid streamId)
    {
        lock (gate)
            return agents.TryGetValue(agentId, out var stream) && stream.StreamId == streamId
                ? stream.Cursors.Select(pair => new ServiceLogCursor(pair.Key, pair.Value)).ToArray()
                : [];
    }

    /// <summary>
    /// Lines numbered above <paramref name="after"/>, at most <paramref name="limit"/> of the newest. With
    /// <paramref name="wait"/> set, waits for new lines when there are none yet.
    /// </summary>
    public async Task<ServiceLogPage> ReadAsync(Guid serviceId, long after, int limit, TimeSpan wait,
        CancellationToken cancellationToken)
    {
        Task? waiting;
        lock (gate)
        {
            var page = Read(serviceId, after, limit);
            if (page.Lines.Count > 0 || wait <= TimeSpan.Zero) return page;
            if (!services.TryGetValue(serviceId, out var log)) services[serviceId] = log = new ServiceLog();
            waiting = log.Waiter();
        }

        try
        {
            await waiting.WaitAsync(wait, cancellationToken);
        }
        catch (TimeoutException)
        {
        }

        lock (gate) return Read(serviceId, after, limit);
    }

    public void Forget(Guid serviceId)
    {
        lock (gate) services.Remove(serviceId);
    }

    private ServiceLogPage Read(Guid serviceId, long after, int limit)
    {
        if (!services.TryGetValue(serviceId, out var log) || log.Lines.Count == 0)
            return new ServiceLogPage([], log?.LastNumber ?? 0, after > (log?.LastNumber ?? 0));
        var lines = log.Lines.Where(line => line.Number > after).ToArray();
        if (lines.Length > limit) lines = lines[^limit..];
        // A cursor ahead of the store means the Panel restarted: the browser starts over.
        return new ServiceLogPage(lines, log.LastNumber, after > log.LastNumber);
    }

    private sealed class AgentStream(Guid streamId)
    {
        public Guid StreamId { get; } = streamId;
        public Dictionary<Guid, long> Cursors { get; } = [];
    }

    private sealed class ServiceLog
    {
        // One shared signal per service: every browser following it waits on the same task.
        private TaskCompletionSource? waiter;
        public Queue<ServiceLogLine> Lines { get; } = new();
        public long LastNumber { get; private set; }

        public void Add(string text, DateTimeOffset at)
        {
            if (Lines.Count == MaximumLinesPerService) Lines.Dequeue();
            Lines.Enqueue(new ServiceLogLine(++LastNumber, at, text));
        }

        public Task Waiter() =>
            (waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public TaskCompletionSource? TakeWaiter()
        {
            var taken = waiter;
            waiter = null;
            return taken;
        }
    }
}

internal sealed record ServiceLogLine(long Number, DateTimeOffset ReceivedAtUtc, string Text);

/// <summary><see cref="Reset"/> tells the browser its cursor is stale and it should drop what it shows.</summary>
internal sealed record ServiceLogPage(IReadOnlyList<ServiceLogLine> Lines, long Cursor, bool Reset);
