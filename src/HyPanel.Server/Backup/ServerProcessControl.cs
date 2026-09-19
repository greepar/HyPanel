namespace HyPanel.Server.Backup;

using System.Runtime.InteropServices;

internal sealed partial class ServerProcessControl
{
    public void RestartCurrentProcess()
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("restart_required");
        var executable = Path.GetFullPath(Environment.ProcessPath
            ?? throw new InvalidOperationException("process_path_missing"));
        UnixExec(executable);
    }

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ExecV(string path, nint argv);

    private static unsafe void UnixExec(string path)
    {
        var arg0 = Marshal.StringToCoTaskMemUTF8(path);
        var argv = stackalloc nint[2];
        argv[0] = arg0;
        argv[1] = 0;
        try
        {
            if (ExecV(path, (nint)argv) != 0) throw new InvalidOperationException("server_restart_failed");
        }
        finally { Marshal.FreeCoTaskMem(arg0); }
    }
}
