namespace HyPanel.Agent;

using System.Text.Json;
using HyPanel.Shared.Contracts;

public sealed record AgentCommandStoreState(
    IReadOnlyList<AgentCommandResult> PendingResults,
    IReadOnlyList<Guid> RecentCompletedIds);

public sealed class AgentCommandStateStore(AgentEnrollmentOptions options)
{
    private const string FileName = "command-state.json";
    private string StatePath => Path.Combine(options.DataDirectory, FileName);

    public async Task<AgentCommandStoreState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            return new AgentCommandStoreState([], []);
        }

        try
        {
            var bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);
            var state = JsonSerializer.Deserialize(bytes, AgentSyncJsonSerializerContext.Default.AgentCommandStoreState)
                ?? throw new JsonException("The Agent command state file is empty.");
            if (state.PendingResults is null || state.RecentCompletedIds is null ||
                state.PendingResults.Any(result => result.CommandId == Guid.Empty) ||
                state.RecentCompletedIds.Any(id => id == Guid.Empty))
                throw new InvalidDataException("The Agent command state file is invalid.");
            return state;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            AtomicFile.Quarantine(StatePath);
            return new AgentCommandStoreState([], []);
        }
    }

    public Task SaveAsync(AgentCommandStoreState state, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(
            options.DataDirectory,
            FileName,
            JsonSerializer.SerializeToUtf8Bytes(state, AgentSyncJsonSerializerContext.Default.AgentCommandStoreState),
            cancellationToken);
}
