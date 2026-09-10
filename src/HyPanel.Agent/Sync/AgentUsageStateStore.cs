namespace HyPanel.Agent;

using System.Text.Json;
using HyPanel.Shared.Contracts;

public sealed record AgentUsageStoreState(IReadOnlyList<UsageBatch> PendingBatches);

public sealed class AgentUsageStateStore(AgentEnrollmentOptions options)
{
    public const int MaxPendingBatches = 256;
    public const int MaxRecordsPerBatch = 256;

    private const string FileName = "usage-state.json";
    private readonly SemaphoreSlim stateGate = new(1, 1);
    private readonly object snapshotLock = new();
    private IReadOnlyList<UsageBatch> pendingBatches = [];
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
                    loaded = true;
                }
            }

            return new AgentUsageStoreState(GetPendingBatchesSnapshot());
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
            await PersistAsync(updated, cancellationToken);
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

            await PersistAsync(updated, cancellationToken);
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
            return new AgentUsageStoreState([]);
        }

        var bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);
        return JsonSerializer.Deserialize(bytes, AgentSyncJsonSerializerContext.Default.AgentUsageStoreState)
               ?? throw new InvalidOperationException("The Agent usage state file is invalid.");
    }

    private Task PersistAsync(IReadOnlyList<UsageBatch> batches, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(
            options.DataDirectory,
            FileName,
            JsonSerializer.SerializeToUtf8Bytes(new AgentUsageStoreState(batches),
                AgentSyncJsonSerializerContext.Default.AgentUsageStoreState),
            cancellationToken);

    private static void ValidateState(AgentUsageStoreState state)
    {
        if (state.PendingBatches is null || state.PendingBatches.Count > MaxPendingBatches)
        {
            throw new InvalidOperationException("The Agent usage state file exceeds the pending batch limit.");
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