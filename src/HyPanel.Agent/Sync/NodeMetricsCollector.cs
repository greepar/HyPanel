namespace HyPanel.Agent;

using System.Diagnostics;
using System.Net.NetworkInformation;
using HyPanel.Shared.Contracts;

public sealed class NodeMetricsCollector(AgentEnrollmentOptions options, TimeProvider timeProvider)
{
    private TimeSpan? previousCpuTime;
    private DateTimeOffset? previousSampleAt;

    public NodeMetrics Collect()
    {
        var observedAt = timeProvider.GetUtcNow();
        var cpuTime = GetSystemCpuTime();
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
        var memoryAvailable = memoryTotal == 0 ? 0 : Math.Clamp(memoryTotal - memory.MemoryLoadBytes, 0, memoryTotal);
        var (diskTotal, diskAvailable) = GetDiskMetrics();
        var (networkUpload, networkDownload) = GetNetworkMetrics();
        return new NodeMetrics(
            observedAt,
            Math.Max(0, Environment.TickCount64 / 1000),
            cpuPercent,
            memoryTotal,
            memoryAvailable,
            diskTotal,
            diskAvailable,
            networkUpload,
            networkDownload);
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

    private static TimeSpan GetSystemCpuTime()
    {
        var ticks = 0L;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { ticks = checked(ticks + process.TotalProcessorTime.Ticks); }
                catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
                                                       or System.ComponentModel.Win32Exception or OverflowException)
                {
                    // Processes can exit or become inaccessible between enumeration and sampling.
                }
            }
        }
        return TimeSpan.FromTicks(ticks);
    }

    private static (long Upload, long Download) GetNetworkMetrics()
    {
        long upload = 0;
        long download = 0;
        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                    networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                try
                {
                    var statistics = networkInterface.GetIPv4Statistics();
                    upload = checked(upload + Math.Max(0, statistics.BytesSent));
                    download = checked(download + Math.Max(0, statistics.BytesReceived));
                }
                catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException
                                                       or OverflowException)
                {
                    // A single unsupported interface must not suppress metrics from the rest of the host.
                }
            }
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            return (0, 0);
        }
        return (upload, download);
    }
}
