namespace HyPanel.Agent;

internal static class AtomicFile
{
    public static async Task WriteAsync(string directory, string fileName, byte[] bytes, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, fileName);
        var temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteSyncedAsync(temporary, bytes, cancellationToken);
            RestrictToOwner(temporary);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Writes the bytes and forces them to disk before returning. Without this a rename can reach the disk before the
    /// data does, and a hard power-off leaves an empty file in place of the old one (seen on XFS after a host reset).
    /// </summary>
    public static async Task WriteSyncedAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    public static void Quarantine(string path)
    {
        if (!File.Exists(path)) return;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("State path has no directory.");
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var quarantine = Path.Combine(directory,
            $"{name}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}");
        File.Move(path, quarantine);
    }

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
