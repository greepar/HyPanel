namespace HyPanel.Agent;

internal static class DiskSpace
{
    private const long SafetyMarginBytes = 16L * 1024 * 1024;

    public static void Require(string path, long requiredBytes)
    {
        if (requiredBytes <= 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return;
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < checked(requiredBytes + SafetyMarginBytes))
            throw new IOException("insufficient_space", HResultFromErrorCode(28));
    }

    private static int HResultFromErrorCode(int errorCode) => unchecked((int)0x80070000) | errorCode;
}
