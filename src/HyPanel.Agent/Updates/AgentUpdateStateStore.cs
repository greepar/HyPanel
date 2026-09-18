namespace HyPanel.Agent.Updates;

using System.Text.Json;
using HyPanel.Shared.Contracts;

public sealed record AgentUpdateLocalState(
    Guid UpdateId,
    AgentUpdateStatus Status,
    string TargetVersion,
    string Rid,
    string FileName,
    string Sha256,
    long Size,
    DateTimeOffset StartedAt,
    string PreviousVersion,
    string InstallPath,
    string? LastError = null);

public sealed class AgentUpdateStateStore(AgentEnrollmentOptions options)
{
    internal const string FileName = "agent-update-state.json";
    private string StatePath => Path.Combine(options.DataDirectory, FileName);

    public async Task<AgentUpdateLocalState?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(StatePath, cancellationToken);
            var state = JsonSerializer.Deserialize(bytes, AgentSyncJsonSerializerContext.Default.AgentUpdateLocalState)
                   ?? throw new JsonException("The Agent update state is empty.");
            if (state.UpdateId == Guid.Empty || string.IsNullOrWhiteSpace(state.InstallPath) || state.Size <= 0)
                throw new InvalidDataException("The Agent update state is invalid.");
            return state;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The Agent update state is corrupt; restore it to preserve rollback context.", exception);
        }
    }

    public Task SaveAsync(AgentUpdateLocalState state, CancellationToken cancellationToken) =>
        AtomicFile.WriteAsync(options.DataDirectory, FileName,
            JsonSerializer.SerializeToUtf8Bytes(state, AgentSyncJsonSerializerContext.Default.AgentUpdateLocalState),
            cancellationToken);

    public async Task<AgentUpdateReport?> GetReportAsync(CancellationToken cancellationToken)
    {
        var state = await LoadAsync(cancellationToken);
        return state is null ? null : new AgentUpdateReport(state.UpdateId, state.Status, state.TargetVersion, state.Rid,
            state.StartedAt, state.PreviousVersion, state.LastError);
    }
}
