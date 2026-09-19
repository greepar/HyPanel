namespace HyPanel.Server.Backup;

internal sealed class ServerOperationCoordinator
{
    private string? operation;

    public bool TryBegin(string name) => Interlocked.CompareExchange(ref operation, name, null) is null;

    public void End(string name) => Interlocked.CompareExchange(ref operation, null, name);
}
