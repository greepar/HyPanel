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

        var bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);
        return JsonSerializer.Deserialize(bytes, AgentSyncJsonSerializerContext.Default.AgentCommandStoreState)
            ?? throw new InvalidOperationException("The Agent command state file is invalid.");
    }

    public Task SaveAsync(AgentCommandStoreState state, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(
            options.DataDirectory,
            FileName,
            JsonSerializer.SerializeToUtf8Bytes(state, AgentSyncJsonSerializerContext.Default.AgentCommandStoreState),
            cancellationToken);
}
