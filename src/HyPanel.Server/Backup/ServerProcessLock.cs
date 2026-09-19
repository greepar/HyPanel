namespace HyPanel.Server.Backup;

internal sealed class ServerProcessLock : IDisposable
{
    private readonly FileStream stream;

    private ServerProcessLock(FileStream stream) => this.stream = stream;

    public static ServerProcessLock Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "server.lock");
        try
        {
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            stream.SetLength(0);
            using var writer = new StreamWriter(stream, leaveOpen: true);
            writer.Write(Environment.ProcessId);
            writer.Flush();
            stream.Flush(true);
            return new ServerProcessLock(stream);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("another_hypanel_server_is_running", exception);
        }
    }

    public void Dispose() => stream.Dispose();
}
