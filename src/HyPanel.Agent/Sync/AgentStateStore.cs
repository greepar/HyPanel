using System.Text.Json;
using HyPanel.Shared.Contracts;

namespace HyPanel.Agent;

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
        try
        {
            var state = JsonSerializer.Deserialize(bytes, AgentSyncJsonSerializerContext.Default.AgentLocalState)
                ?? throw new JsonException("The Agent state file is empty.");
            if (state.AppliedRevision < 0 || state.DesiredState is { Revision: < 0 })
                throw new InvalidDataException("The Agent state file is invalid.");
            return state;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            // A torn write (power loss) must not keep the Agent down: set the file aside and start from revision 0,
            // which makes the Panel send the full desired state again.
            AtomicFile.Quarantine(StatePath);
            return new AgentLocalState(0);
        }
    }

    public Task SaveAsync(AgentLocalState state, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(
            options.DataDirectory,
            StateFileName,
            JsonSerializer.SerializeToUtf8Bytes(state, AgentSyncJsonSerializerContext.Default.AgentLocalState),
            cancellationToken);

}
