namespace HyPanel.Agent;

using System.Text.Json;
using HyPanel.Shared.Contracts;

public sealed record AgentLocalState(long AppliedRevision, NodeDesiredState? DesiredState = null);

public sealed class AgentStateStore(AgentEnrollmentOptions options)
{
    private const string StateFileName = "state.json";
    private string StatePath => Path.Combine(options.DataDirectory, StateFileName);

    public async Task<AgentLocalState> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            return new AgentLocalState(0);
        }

        var bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);
        return JsonSerializer.Deserialize(bytes, AgentSyncJsonSerializerContext.Default.AgentLocalState)
            ?? throw new InvalidOperationException("The Agent state file is invalid.");
    }

    public Task SaveAsync(AgentLocalState state, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(
            options.DataDirectory,
            StateFileName,
            JsonSerializer.SerializeToUtf8Bytes(state, AgentSyncJsonSerializerContext.Default.AgentLocalState),
            cancellationToken);
}
