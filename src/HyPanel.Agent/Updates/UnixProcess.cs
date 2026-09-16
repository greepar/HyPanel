namespace HyPanel.Agent.Updates;

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

internal static partial class UnixProcess
{
    public static unsafe void ReplaceCurrentProcess(string executable)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var path = ToUtf8(executable);
        var argv = NativeMemory.Alloc((nuint)(2 * sizeof(nint)));
        try
        {
            ((nint*)argv)[0] = (nint)path;
            ((nint*)argv)[1] = 0;
            _ = ExecV((nint)path, (nint)argv);
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "execv failed.");
        }
        finally
        {
            NativeMemory.Free(argv);
            NativeMemory.Free(path);
        }
    }

    private static unsafe byte* ToUtf8(string value)
    {
        var length = Encoding.UTF8.GetByteCount(value);
        var bytes = (byte*)NativeMemory.Alloc((nuint)(length + 1));
        Encoding.UTF8.GetBytes(value, new Span<byte>(bytes, length));
        bytes[length] = 0;
        return bytes;
    }

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true)]
    private static partial int ExecV(nint path, nint argv);
}
