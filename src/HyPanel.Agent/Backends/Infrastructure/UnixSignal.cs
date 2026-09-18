namespace HyPanel.Agent.Backends.Infrastructure;

using System.Runtime.InteropServices;

internal static partial class UnixSignal
{
    private const int SigTerm = 15;

    public static bool TryTerminate(int processId)
    {
        if (OperatingSystem.IsWindows()) return false;
        return Kill(processId, SigTerm) == 0;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int processId, int signal);
}
