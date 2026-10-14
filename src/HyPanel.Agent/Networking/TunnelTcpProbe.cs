namespace HyPanel.Agent.Networking;

using System.Net;
using System.Net.Sockets;

/// <param name="Success">A TCP connection through the tunnel's exit completed.</param>
/// <param name="Rejected">Every attempt failed with an ICMP-style refusal (host/network unreachable, access denied),
/// the signature of a firewall on the exit rejecting forwarded traffic.</param>
public sealed record TunnelTcpProbeResult(bool Success, bool Rejected);

/// <summary>
/// ICMP echo only proves the tunnel carries packets addressed to the exit itself. This opens a real TCP connection
/// bound to the tunnel source address, which the policy route sends out through the exit, so it also exercises the
/// exit's forwarding, NAT and firewall.
/// </summary>
public static class TunnelTcpProbe
{
    private static readonly IPEndPoint[] DefaultTargets =
    [
        new(IPAddress.Parse("1.1.1.1"), 443), new(IPAddress.Parse("8.8.8.8"), 443),
        new(IPAddress.Parse("9.9.9.9"), 443), new(IPAddress.Parse("223.5.5.5"), 443)
    ];

    /// <summary>Succeeds as soon as any target accepts a connection.</summary>
    public static async Task<TunnelTcpProbeResult> CheckAsync(IPAddress source, TimeSpan timeout, CancellationToken ct,
        IReadOnlyList<IPEndPoint>? targets = null,
        Func<IPAddress, IPEndPoint, CancellationToken, Task>? connect = null)
    {
        connect ??= ConnectAsync;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var attempts = (targets ?? DefaultTargets).Select(target => AttemptAsync(connect, source, target, cts.Token)).ToList();
        var refused = 0;
        var total = attempts.Count;
        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);
            var outcome = await finished;
            if (outcome == SocketError.Success)
            {
                await cts.CancelAsync();
                return new(true, false);
            }
            if (outcome is SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.AccessDenied) refused++;
        }
        ct.ThrowIfCancellationRequested();
        return new(false, total > 0 && refused == total);
    }

    private static async Task<SocketError> AttemptAsync(Func<IPAddress, IPEndPoint, CancellationToken, Task> connect,
        IPAddress source, IPEndPoint target, CancellationToken ct)
    {
        try
        {
            await connect(source, target, ct);
            return SocketError.Success;
        }
        catch (SocketException exception) { return exception.SocketErrorCode; }
        catch (OperationCanceledException) { return SocketError.TimedOut; }
    }

    private static async Task ConnectAsync(IPAddress source, IPEndPoint target, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(source, 0));
        await socket.ConnectAsync(target, ct);
    }
}
