using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using HyPanel.Shared.Contracts;

namespace HyPanel.Agent.Backends.Infrastructure;

public sealed record BackendProcessStatus(
    Guid ServiceId,
    ServiceRuntimeStatus Status,
    int? ProcessId,
    int ExitCode,
    IReadOnlyList<string> RecentLogs,
    string? ErrorMessage);

public sealed class BackendProcessSupervisor(ILogger<BackendProcessSupervisor> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, ManagedProcess> processes = new();

    /// <summary>Raised (from an output reader thread) whenever a backend writes a new line.</summary>
    public event Action? LogWritten;

    /// <summary>
    /// Lines numbered above <paramref name="after"/> for every service that has some, oldest first. Numbers start at
    /// 1 per service when this process starts and survive backend restarts.
    /// </summary>
    public IReadOnlyList<(Guid ServiceId, long FirstSequence, IReadOnlyList<string> Lines)> LogsAfter(
        IReadOnlyDictionary<Guid, long> after)
    {
        var result = new List<(Guid, long, IReadOnlyList<string>)>();
        foreach (var (serviceId, managed) in processes)
        {
            var (first, lines) = managed.LogsAfter(after.GetValueOrDefault(serviceId));
            if (lines.Count > 0) result.Add((serviceId, first, lines));
        }
        return result;
    }

    public async Task<BackendProcessStatus> StartAsync(Guid serviceId, BackendProcessSpec spec,
        CancellationToken cancellationToken)
    {
        var managed = processes.GetOrAdd(serviceId, id => new ManagedProcess(id, () => LogWritten?.Invoke()));
        await managed.Gate.WaitAsync(cancellationToken);
        try
        {
            if (managed.Process is { HasExited: false }) return managed.Status(ServiceRuntimeStatus.Running);
            managed.Process?.Dispose();
            managed.Process = null;
            ValidateSpec(spec);
            var startInfo = new ProcessStartInfo(spec.FileName)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, WorkingDirectory = spec.WorkingDirectory
            };
            foreach (var argument in spec.Arguments) startInfo.ArgumentList.Add(argument);
            foreach (var pair in spec.Environment)
            {
                if (!IsSafeEnvironmentKey(pair.Key) || pair.Value.IndexOf('\0') >= 0)
                    throw new InvalidDataException("Backend environment contains an invalid entry.");
                startInfo.Environment[pair.Key] = pair.Value;
            }

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            managed.IntentionalStop = false;
            process.Exited += (_, _) =>
            {
                managed.ExitCode = process.ExitCode;
                managed.Error = managed.IntentionalStop || process.ExitCode == 0
                    ? null
                    : $"Backend process exited with code {process.ExitCode}.";
            };
            if (!process.Start()) throw new InvalidOperationException("Backend process did not start.");
            managed.Process = process;
            managed.Error = null;
            _ = CaptureOutputAsync(process.StandardOutput, "stdout", managed);
            _ = CaptureOutputAsync(process.StandardError, "stderr", managed);
            return managed.Status(ServiceRuntimeStatus.Running);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException or ArgumentException)
        {
            managed.Error = exception.Message;
            logger.LogError(exception, "Could not start backend service {ServiceId}.", serviceId);
            return managed.Status(ServiceRuntimeStatus.Failed);
        }
        finally
        {
            managed.Gate.Release();
        }
    }

    public async Task<BackendProcessStatus> StopAsync(Guid serviceId, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!processes.TryGetValue(serviceId, out var managed))
            return new BackendProcessStatus(serviceId, ServiceRuntimeStatus.Stopped, null, 0, [], null);
        await managed.Gate.WaitAsync(cancellationToken);
        try
        {
            var process = managed.Process;
            if (process is null || process.HasExited) return managed.Status(ServiceRuntimeStatus.Stopped);
            managed.IntentionalStop = true;
            if (OperatingSystem.IsWindows())
                _ = process.CloseMainWindow();
            else
                _ = UnixSignal.TryTerminate(process.Id);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }

            managed.Error = null;
            return managed.Status(ServiceRuntimeStatus.Stopped);
        }
        catch (Exception exception) when
            (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            managed.Error = exception.Message;
            logger.LogWarning(exception, "Could not stop backend service {ServiceId}.", serviceId);
            return managed.Status(ServiceRuntimeStatus.Failed);
        }
        finally
        {
            managed.Gate.Release();
        }
    }

    public async Task<BackendProcessStatus> RestartAsync(Guid serviceId, BackendProcessSpec spec, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        await StopAsync(serviceId, timeout, cancellationToken);
        return await StartAsync(serviceId, spec, cancellationToken);
    }

    public BackendProcessStatus GetStatus(Guid serviceId)
    {
        if (!processes.TryGetValue(serviceId, out var managed))
        {
            return new(serviceId, ServiceRuntimeStatus.Stopped, null, 0, [], null);
        }

        var status = managed.Process is { HasExited: false }
            ? ServiceRuntimeStatus.Running
            : managed.Error is null
                ? ServiceRuntimeStatus.Stopped
                : ServiceRuntimeStatus.Failed;
        return managed.Status(status);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync(CancellationToken.None);
        foreach (var managed in processes.Values)
        {
            managed.Process?.Dispose();
            managed.Gate.Dispose();
        }

        processes.Clear();
    }

    public async Task StopAllAsync(CancellationToken cancellationToken)
    {
        foreach (var serviceId in processes.Keys)
            await StopAsync(serviceId, TimeSpan.FromSeconds(5), cancellationToken);
    }

    private static void ValidateSpec(BackendProcessSpec spec)
    {
        if (!Path.IsPathFullyQualified(spec.FileName) || !File.Exists(spec.FileName) ||
            !Directory.Exists(spec.WorkingDirectory) || spec.Arguments.Any(argument => argument.IndexOf('\0') >= 0))
            throw new InvalidDataException("Backend process specification is invalid.");
    }

    private async Task CaptureOutputAsync(StreamReader reader, string stream, ManagedProcess managed)
    {
        try
        {
            var line = new StringBuilder(ManagedProcess.MaxLogEntryCharacters);
            var buffer = new char[1];
            var overflowed = false;
            while (await reader.ReadAsync(buffer) != 0)
            {
                var character = buffer[0];
                if (character == '\n')
                {
                    managed.AddLog(overflowed ? $"{stream}: [line omitted: exceeded limit]" : $"{stream}: {line.ToString().TrimEnd('\r')}");
                    line.Clear();
                    overflowed = false;
                }
                else if (!overflowed && line.Length < ManagedProcess.MaxLogEntryCharacters)
                {
                    line.Append(character);
                }
                else
                {
                    line.Clear();
                    overflowed = true;
                }
            }
            if (overflowed || line.Length > 0)
                managed.AddLog(overflowed ? $"{stream}: [line omitted: exceeded limit]" : $"{stream}: {line.ToString().TrimEnd('\r')}");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            logger.LogDebug(exception, "Stopped collecting backend output for {ServiceId}.", managed.ServiceId);
        }
    }

    private static bool IsSafeEnvironmentKey(string key) => !string.IsNullOrEmpty(key) &&
                                                            key.All(character =>
                                                                char.IsAsciiLetterOrDigit(character) ||
                                                                character == '_');

    private sealed class ManagedProcess(Guid serviceId, Action? logWritten = null)
    {
        private const int MaxLogEntries = 512;
        internal const int MaxLogEntryCharacters = 4096;
        private readonly Queue<string> logs = new();
        private long lastSequence;
        public Guid ServiceId { get; } = serviceId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Process? Process { get; set; }
        public int ExitCode { get; set; }
        public bool IntentionalStop { get; set; }
        public string? Error { get; set; }

        public void AddLog(string line)
        {
            if (line.Length > MaxLogEntryCharacters) line = "[line omitted: exceeded limit]";
            lock (logs)
            {
                if (logs.Count == MaxLogEntries) logs.Dequeue();
                logs.Enqueue(line);
                lastSequence++;
            }
            logWritten?.Invoke();
        }

        public (long FirstSequence, IReadOnlyList<string> Lines) LogsAfter(long after)
        {
            lock (logs)
            {
                var oldest = lastSequence - logs.Count + 1;
                // A cursor behind the buffer (lines rolled out unsent) sends everything still held.
                var skip = after >= oldest && after <= lastSequence ? after - oldest + 1 : 0;
                var lines = logs.Skip((int)skip).ToArray();
                return (oldest + skip, lines);
            }
        }

        public BackendProcessStatus Status(ServiceRuntimeStatus status)
        {
            lock (logs)
                return new(ServiceId, status, Process is { HasExited: false } ? Process.Id : null, ExitCode,
                    [.. logs], Error);
        }
    }
}
