namespace HyPanel.Agent;

using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Shared.Contracts;

public sealed record UserTrafficCounter(Guid UserId, Guid ServiceId, long UploadBytes, long DownloadBytes);

public sealed record AgentUsageStoreState(IReadOnlyList<UsageBatch> PendingBatches,
    IReadOnlyList<UserTrafficCounter>? LastObserved = null);

public sealed class AgentUsageStateStore(AgentEnrollmentOptions options)
{
    public const int MaxPendingBatches = 256;
    public const int MaxRecordsPerBatch = 256;

    private const string FileName = "usage-state.json";
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly object snapshotLock = new();
    private IReadOnlyList<UsageBatch> pendingBatches = [];
    private IReadOnlyList<UserTrafficCounter> lastObserved = [];
    private bool loaded;

    private string StatePath => Path.Combine(options.DataDirectory, FileName);

    public async Task<AgentUsageStoreState> LoadAsync(CancellationToken cancellationToken)
    {
        await stateGate.WaitAsync(cancellationToken);
        try
        {
            if (!loaded)
            {
                var state = await ReadStateAsync(cancellationToken);
                ValidateState(state);
                lock (snapshotLock)
                {
                    pendingBatches = state.PendingBatches.ToArray();
                    lastObserved = state.LastObserved?.ToArray() ?? [];
                    loaded = true;
                }
            }

            return new AgentUsageStoreState(GetPendingBatchesSnapshot(), GetLastObservedSnapshot());
        }
        finally
        {
            stateGate.Release();
        }
    }

    public async Task RecordCumulativeAsync(Guid serviceId, IReadOnlyList<BackendUserTraffic> observations,
        CancellationToken cancellationToken)
    {
        if (serviceId == Guid.Empty || observations is null) throw new ArgumentException("Invalid traffic observations.");
        await EnsureLoadedAsync(cancellationToken);
        await stateGate.WaitAsync(cancellationToken);
        try
        {
            var counters = lastObserved.ToDictionary(item => (item.ServiceId, item.UserId));
            var deltas = new List<UserUsageDelta>();
            foreach (var observation in observations)
            {
                if (observation.UserId == Guid.Empty || observation.UploadBytes < 0 || observation.DownloadBytes < 0)
                    throw new InvalidOperationException("A backend returned invalid traffic counters.");
                counters.TryGetValue((serviceId, observation.UserId), out var previous);
                if (previous is not null)
                {
                    var upload = observation.UploadBytes >= previous.UploadBytes
                        ? observation.UploadBytes - previous.UploadBytes
                        : 0;
                    var download = observation.DownloadBytes >= previous.DownloadBytes
                        ? observation.DownloadBytes - previous.DownloadBytes
                        : 0;
                    if (upload > 0 || download > 0)
                        deltas.Add(new UserUsageDelta(observation.UserId, serviceId, upload, download));
                }
                counters[(serviceId, observation.UserId)] = new UserTrafficCounter(observation.UserId, serviceId,
                    observation.UploadBytes, observation.DownloadBytes);
            }

            var batches = pendingBatches;
            if (deltas.Count > 0)
            {
                if (batches.Count >= MaxPendingBatches)
                    throw new InvalidOperationException("The pending usage queue is full.");
                batches = batches.Append(new UsageBatch(Guid.NewGuid(),
                    observations.Max(item => item.ObservedAt), deltas)).ToArray();
            }
            var observed = counters.Values.OrderBy(item => item.ServiceId).ThenBy(item => item.UserId).ToArray();
            var state = new AgentUsageStoreState(batches, observed);
            ValidateState(state);
            await PersistAsync(state, cancellationToken);
            lock (snapshotLock)
            {
                pendingBatches = batches;
                lastObserved = observed;
            }
        }
        finally
        {
            stateGate.Release();
        }
    }

    public async Task EnsureBaselinesAsync(Guid serviceId, IReadOnlyList<BackendUser>? users,
        CancellationToken cancellationToken)
    {
        if (serviceId == Guid.Empty || users is null) return;
        await EnsureLoadedAsync(cancellationToken);
        await stateGate.WaitAsync(cancellationToken);
        try
        {
            var counters = lastObserved.ToDictionary(item => (item.ServiceId, item.UserId));
            var changed = false;
            foreach (var user in users)
            {
                if (user.UserId == Guid.Empty) throw new InvalidOperationException("A desired backend user is invalid.");
                changed |= counters.TryAdd((serviceId, user.UserId),
                    new UserTrafficCounter(user.UserId, serviceId, 0, 0));
            }
            if (!changed) return;
            var observed = counters.Values.OrderBy(item => item.ServiceId).ThenBy(item => item.UserId).ToArray();
            var state = new AgentUsageStoreState(pendingBatches, observed);
            ValidateState(state);
            await PersistAsync(state, cancellationToken);
            lock (snapshotLock) lastObserved = observed;
        }
        finally
        {
            stateGate.Release();
        }
    }

    public IReadOnlyList<UsageBatch> GetPendingBatchesSnapshot()
    {
        lock (snapshotLock)
        {
            if (!loaded)
            {
                throw new InvalidOperationException("Usage state must be loaded before it can be read.");
            }

            return pendingBatches.ToArray();
        }
    }

    public async Task EnqueueAsync(UsageBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        await EnsureLoadedAsync(cancellationToken);

        await stateGate.WaitAsync(cancellationToken);
        try
        {
            IReadOnlyList<UsageBatch> updated;
            lock (snapshotLock)
            {
                if (pendingBatches.Any(item => item.BatchId == batch.BatchId))
                {
                    throw new InvalidOperationException("A pending usage batch already has this BatchId.");
                }

                updated = pendingBatches.Append(batch).ToArray();
            }

            ValidateState(new AgentUsageStoreState(updated));
            await PersistAsync(new AgentUsageStoreState(updated, lastObserved), cancellationToken);
            lock (snapshotLock)
            {
                pendingBatches = updated;
            }
        }
        finally
        {
            stateGate.Release();
        }
    }

    public async Task AcknowledgeAsync(IReadOnlyList<UsageBatch> sentSnapshot,
        IReadOnlyList<Guid>? acceptedBatchIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sentSnapshot);
        ValidateAcknowledgements(sentSnapshot, acceptedBatchIds);
        await EnsureLoadedAsync(cancellationToken);

        await stateGate.WaitAsync(cancellationToken);
        try
        {
            var acceptedIds = acceptedBatchIds!.ToHashSet();
            IReadOnlyList<UsageBatch> updated;
            var changed = false;
            lock (snapshotLock)
            {
                updated = pendingBatches.Where(batch => !acceptedIds.Contains(batch.BatchId)).ToArray();
                changed = updated.Count != pendingBatches.Count;
            }

            if (!changed)
            {
                return;
            }

            await PersistAsync(new AgentUsageStoreState(updated, lastObserved), cancellationToken);
            lock (snapshotLock)
            {
                pendingBatches = updated;
            }
        }
        finally
        {
            stateGate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (!loaded)
        {
            await LoadAsync(cancellationToken);
        }
    }

    private async Task<AgentUsageStoreState> ReadStateAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            return new AgentUsageStoreState([], []);
        }

        var bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);
        return JsonSerializer.Deserialize(bytes, AgentSyncJsonSerializerContext.Default.AgentUsageStoreState)
               ?? throw new InvalidOperationException("The Agent usage state file is invalid.");
    }

    private Task PersistAsync(AgentUsageStoreState state, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(
            options.DataDirectory,
            FileName,
            JsonSerializer.SerializeToUtf8Bytes(state,
                AgentSyncJsonSerializerContext.Default.AgentUsageStoreState),
            cancellationToken);

    private static void ValidateState(AgentUsageStoreState state)
    {
        if (state.PendingBatches is null || state.PendingBatches.Count > MaxPendingBatches)
        {
            throw new InvalidOperationException("The Agent usage state file exceeds the pending batch limit.");
        }

        var counterKeys = new HashSet<(Guid ServiceId, Guid UserId)>();
        foreach (var counter in state.LastObserved ?? [])
        {
            if (counter.UserId == Guid.Empty || counter.ServiceId == Guid.Empty || counter.UploadBytes < 0 ||
                counter.DownloadBytes < 0 || !counterKeys.Add((counter.ServiceId, counter.UserId)))
                throw new InvalidOperationException("The Agent usage counter state is invalid.");
        }

        var batchIds = new HashSet<Guid>();
        foreach (var batch in state.PendingBatches)
        {
            if (batch is null || batch.BatchId == Guid.Empty || batch.ObservedAt.Offset != TimeSpan.Zero ||
                batch.Records is null ||
                batch.Records.Count > MaxRecordsPerBatch || !batchIds.Add(batch.BatchId))
            {
                throw new InvalidOperationException("The Agent usage state file is invalid.");
            }

            if (batch.Records.Any(record => record.UserId == Guid.Empty || record.ServiceId == Guid.Empty ||
                                            record.UploadBytes < 0 || record.DownloadBytes < 0))
            {
                throw new InvalidOperationException("The Agent usage state file contains an invalid record.");
            }
        }
    }

    private IReadOnlyList<UserTrafficCounter> GetLastObservedSnapshot()
    {
        lock (snapshotLock) return lastObserved.ToArray();
    }

    private static void ValidateAcknowledgements(IReadOnlyList<UsageBatch> sentSnapshot,
        IReadOnlyList<Guid>? acceptedBatchIds)
    {
        if (acceptedBatchIds is null)
        {
            throw new JsonException("The sync response does not contain usage acknowledgements.");
        }

        var sentIds = sentSnapshot.Select(batch => batch.BatchId).ToHashSet();
        var acceptedIds = new HashSet<Guid>();
        foreach (var batchId in acceptedBatchIds)
        {
            if (batchId == Guid.Empty || !acceptedIds.Add(batchId) || !sentIds.Contains(batchId))
            {
                throw new JsonException("The sync response contains invalid usage acknowledgements.");
            }
        }
    }
}
