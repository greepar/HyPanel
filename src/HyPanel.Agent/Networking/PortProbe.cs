using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace HyPanel.Agent.Networking;

/// <summary>A listening port some other program already holds, with that program when it can be identified.</summary>
public sealed record PortHolder(string Protocol, int Port, int? ProcessId, string? ProcessName)
{
    /// <summary>Shown to the admin as the service's error, e.g. <c>TCP 443 已被 nginx（PID 812）占用</c>.</summary>
    public string Describe() => ProcessName is not null
        ? $"{Protocol.ToUpperInvariant()} {Port} 已被 {ProcessName}（PID {ProcessId}）占用"
        : $"{Protocol.ToUpperInvariant()} {Port} 已被其他程序占用（无权限识别，可在节点上运行 ss -lntup | grep :{Port} 查看）";
}

/// <summary>
/// Checks, before a backend is started, that its ports are free, so a taken port is reported as such instead of
/// as a backend that exited for no visible reason.
/// </summary>
public static class PortProbe
{
    /// <summary>
    /// The first port that cannot be bound, skipping ports held by <paramref name="ownProcessIds"/> (backends this
    /// Agent runs). The caller leaves out the ports the service's running instance holds itself, since a backend
    /// with ambient capabilities may not show up in the <c>/proc</c> lookup. Null when every port is free.
    /// </summary>
    public static PortHolder? FindTaken(IEnumerable<int> tcpPorts, IEnumerable<int> udpPorts,
        IReadOnlySet<int> ownProcessIds)
    {
        foreach (var (protocol, port) in tcpPorts.Select(port => ("tcp", port)).Concat(udpPorts.Select(port => ("udp", port))))
        {
            if (CanBind(protocol, port)) continue;
            var holder = FindHolder(protocol, port);
            if (holder.ProcessId is { } pid && ownProcessIds.Contains(pid)) continue;
            return holder;
        }
        return null;
    }

    internal static bool CanBind(string protocol, int port)
    {
        // Both families: a program may hold only the IPv4 or only the IPv6 side of the port.
        return TryBind(AddressFamily.InterNetwork, protocol, port)
               && (!Socket.OSSupportsIPv6 || TryBind(AddressFamily.InterNetworkV6, protocol, port));
    }

    private static bool TryBind(AddressFamily family, string protocol, int port)
    {
        try
        {
            using var socket = protocol == "tcp"
                ? new Socket(family, SocketType.Stream, ProtocolType.Tcp)
                : new Socket(family, SocketType.Dgram, ProtocolType.Udp);
            // Without SO_REUSEADDR the bind fails while anything else is bound, as the backend's own bind would.
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            if (family == AddressFamily.InterNetworkV6) socket.DualMode = false;
            socket.Bind(new IPEndPoint(family == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, port));
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return false;
        }
        catch (SocketException)
        {
            // No IPv6 on this host, or a permission problem unrelated to the port being taken.
            return true;
        }
    }

    /// <summary>
    /// Finds the process through the socket inode in <c>/proc/net</c> and the <c>/proc/&lt;pid&gt;/fd</c> links. Other
    /// users' descriptors are only readable with root, so the program may stay unknown.
    /// </summary>
    internal static PortHolder FindHolder(string protocol, int port)
    {
        if (!OperatingSystem.IsLinux()) return new PortHolder(protocol, port, null, null);
        var inodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in protocol == "tcp" ? new[] { "/proc/net/tcp", "/proc/net/tcp6" } : ["/proc/net/udp", "/proc/net/udp6"])
            foreach (var inode in SocketInodes(table, port, protocol == "tcp")) inodes.Add(inode);
        if (inodes.Count == 0) return new PortHolder(protocol, port, null, null);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), NumberStyles.None, CultureInfo.InvariantCulture, out var pid)) continue;
                try
                {
                    foreach (var link in Directory.EnumerateFileSystemEntries(Path.Combine(directory, "fd")))
                    {
                        var target = new FileInfo(link).LinkTarget;
                        if (target is null || !target.StartsWith("socket:[", StringComparison.Ordinal)) continue;
                        if (!inodes.Contains(target[8..^1])) continue;
                        var name = File.ReadAllText(Path.Combine(directory, "comm")).Trim();
                        return new PortHolder(protocol, port, pid, name.Length == 0 ? null : name);
                    }
                }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
                {
                    // Another user's process, or one that just exited.
                }
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
        }
        return new PortHolder(protocol, port, null, null);
    }

    /// <summary>Inodes of sockets bound to <paramref name="port"/>; for TCP only listening ones (state 0A).</summary>
    internal static IEnumerable<string> SocketInodes(string table, int port, bool listeningOnly)
    {
        string[] lines;
        try { lines = File.ReadAllLines(table); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines.Skip(1))
        {
            // sl local_address rem_address st tx_queue:rx_queue tr:tm->when retrnsmt uid timeout inode
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 10) continue;
            var colon = fields[1].LastIndexOf(':');
            if (colon < 0 || !int.TryParse(fields[1][(colon + 1)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var local)
                || local != port) continue;
            if (listeningOnly && fields[3] != "0A") continue;
            if (fields[9] != "0") yield return fields[9];
        }
    }
}
