namespace HyPanel.Agent;

using System.Diagnostics;
using HyPanel.Shared.Contracts;

public sealed class NodeMetricsCollector(AgentEnrollmentOptions options, TimeProvider timeProvider)
{
    private TimeSpan? previousCpuTime;
    private DateTimeOffset? previousSampleAt;

    public NodeMetrics Collect()
    {
        var observedAt = timeProvider.GetUtcNow();
        using var process = Process.GetCurrentProcess();
        var cpuTime = process.TotalProcessorTime;
        var cpuPercent = 0d;
        if (previousCpuTime is { } priorCpu && previousSampleAt is { } priorTime)
        {
            var elapsed = observedAt - priorTime;
            if (elapsed > TimeSpan.Zero)
            {
                cpuPercent = Math.Clamp(
                    (cpuTime - priorCpu).TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100d,
                    0d,
                    100d);
            }
        }

        previousCpuTime = cpuTime;
        previousSampleAt = observedAt;
        var memory = GC.GetGCMemoryInfo();
        var memoryTotal = Math.Max(0, memory.TotalAvailableMemoryBytes);
        var memoryAvailable = memoryTotal == 0 ? 0 : Math.Max(0, memoryTotal - GC.GetTotalMemory(forceFullCollection: false));
        var (diskTotal, diskAvailable) = GetDiskMetrics();
        return new NodeMetrics(
            observedAt,
            GetUptimeSeconds(process, observedAt),
            cpuPercent,
            memoryTotal,
            memoryAvailable,
            diskTotal,
            diskAvailable,
            0,
            0);
    }

    private (long Total, long Available) GetDiskMetrics()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(options.DataDirectory));
            if (string.IsNullOrEmpty(root)) return (0, 0);
            var drive = new DriveInfo(root);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (IOException)
        {
            return (0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    private static long GetUptimeSeconds(Process process, DateTimeOffset observedAt)
    {
        try
        {
            return Math.Max(0, (long)(observedAt - process.StartTime.ToUniversalTime()).TotalSeconds);
        }
        catch (InvalidOperationException)
        {
            return Math.Max(0, Environment.TickCount64 / 1000);
        }
    }
}
