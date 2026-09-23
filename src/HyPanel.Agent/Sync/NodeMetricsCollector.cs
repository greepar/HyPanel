namespace HyPanel.Agent;

using System.Diagnostics;
using System.Net.NetworkInformation;
using HyPanel.Shared.Contracts;

public sealed class NodeMetricsCollector(AgentEnrollmentOptions options, TimeProvider timeProvider)
{
    private TimeSpan? previousCpuTime;
    private DateTimeOffset? previousSampleAt;
    private long? previousLinuxBusyTicks;
    private long? previousLinuxTotalTicks;

    public NodeMetrics Collect()
    {
        var observedAt = timeProvider.GetUtcNow();
        var cpuPercent = OperatingSystem.IsLinux()
            ? CollectLinuxCpuPercent()
            : CollectPortableCpuPercent(observedAt);
        previousSampleAt = observedAt;
        var memory = GC.GetGCMemoryInfo();
        var memoryTotal = Math.Max(0, memory.TotalAvailableMemoryBytes);
        var memoryAvailable = memoryTotal == 0 ? 0 : Math.Clamp(memoryTotal - memory.MemoryLoadBytes, 0, memoryTotal);
        var (diskTotal, diskAvailable) = GetDiskMetrics();
        var (networkUpload, networkDownload) = GetNetworkMetrics();
        var (tcpPorts, udpPorts) = GetListeningPorts();
        return new NodeMetrics(
            observedAt,
            Math.Max(0, Environment.TickCount64 / 1000),
            cpuPercent,
            memoryTotal,
            memoryAvailable,
            diskTotal,
            diskAvailable,
            networkUpload,
            networkDownload,
            tcpPorts,
            udpPorts);
    }

    internal const int MaximumReportedPorts = 4096;

    /// <summary>Ports already bound on this host, so the Panel can suggest a free listen port for new services.</summary>
    private static (int[]? Tcp, int[]? Udp) GetListeningPorts()
    {
        try
        {
            var properties = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            return (Ports(properties.GetActiveTcpListeners()), Ports(properties.GetActiveUdpListeners()));
        }
        catch (Exception exception) when (exception is System.Net.NetworkInformation.NetworkInformationException
                                              or PlatformNotSupportedException or IOException
                                              or UnauthorizedAccessException)
        {
            return (null, null);
        }

        static int[] Ports(System.Net.IPEndPoint[] endpoints) => endpoints.Select(endpoint => endpoint.Port)
            .Where(port => port is >= 1 and <= 65_535).Distinct().Order().Take(MaximumReportedPorts).ToArray();
    }

    private double CollectPortableCpuPercent(DateTimeOffset observedAt)
    {
        var cpuTime = GetSystemCpuTimeSafely();
        var percent = 0d;
        if (previousCpuTime is { } priorCpu && previousSampleAt is { } priorTime)
        {
            var elapsed = observedAt - priorTime;
            if (elapsed > TimeSpan.Zero)
            {
                percent = Math.Clamp(
                    (cpuTime - priorCpu).TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100d,
                    0d,
                    100d);
            }
        }

        previousCpuTime = cpuTime;
        return percent;
    }

    private double CollectLinuxCpuPercent()
    {
        if (!TryReadLinuxCpuTicks(out var busy, out var total)) return 0d;
        var percent = 0d;
        if (previousLinuxBusyTicks is { } priorBusy && previousLinuxTotalTicks is { } priorTotal)
        {
            var busyDelta = busy - priorBusy;
            var totalDelta = total - priorTotal;
            if (busyDelta >= 0 && totalDelta > 0)
                percent = Math.Clamp((double)busyDelta / totalDelta * 100d, 0d, 100d);
        }
        previousLinuxBusyTicks = busy;
        previousLinuxTotalTicks = total;
        return percent;
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
        // Process enumeration can crash a NativeAOT process launched by launchd on macOS 26.
        // Report CPU as unavailable there instead of risking the entire Agent.
        if (OperatingSystem.IsMacOS()) return TimeSpan.Zero;

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

    private static TimeSpan GetSystemCpuTimeSafely()
    {
        try
        {
            return GetSystemCpuTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException
                                               or System.ComponentModel.Win32Exception or PlatformNotSupportedException
                                               or OverflowException)
        {
            return TimeSpan.Zero;
        }
    }

    private static bool TryReadLinuxCpuTicks(out long busy, out long total)
    {
        busy = 0;
        total = 0;
        try
        {
            using var reader = new StreamReader("/proc/stat");
            var line = reader.ReadLine();
            if (line is null || !line.StartsWith("cpu ", StringComparison.Ordinal)) return false;
            var fields = line.AsSpan(4).Trim().ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 8) return false;
            Span<long> ticks = stackalloc long[8];
            for (var index = 0; index < ticks.Length; index++)
                if (!long.TryParse(fields[index], out ticks[index]) || ticks[index] < 0) return false;
            var idle = checked(ticks[3] + ticks[4]);
            total = 0;
            foreach (var value in ticks) total = checked(total + value);
            busy = total - idle;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OverflowException)
        {
            return false;
        }
    }

    private static (long Upload, long Download) GetNetworkMetrics()
    {
        // Interface enumeration has the same launchd/NativeAOT failure mode as process
        // enumeration on macOS 26, but aborts the process instead of throwing.
        if (OperatingSystem.IsMacOS()) return (0, 0);

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
