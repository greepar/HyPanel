namespace HyPanel.Agent.Networking;

using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// ICMP echo bound to a tunnel source address, equivalent to <c>ping -I</c> without the external binary.
/// Uses a raw socket (CAP_NET_RAW) and falls back to the unprivileged ICMP datagram socket
/// (net.ipv4.ping_group_range).
/// </summary>
public static class IcmpProbe
{
    public sealed record Result(int Sent, int Received, TimeSpan[] RoundTrips, string? Error)
    {
        public bool Success => Received > 0;
    }

    public static async Task<Result> PingAsync(IPAddress local, IPAddress peer, int count, TimeSpan timeout, CancellationToken ct)
    {
        Socket socket;
        var raw = true;
        try { socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp); }
        catch (SocketException)
        {
            raw = false;
            try { socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Icmp); }
            catch (SocketException ex) { return new(0, 0, [], $"无法创建 ICMP 套接字（需要 CAP_NET_RAW 或 ping_group_range）：{ex.Message}"); }
        }
        using (socket)
        {
            try { socket.Bind(new IPEndPoint(local, 0)); }
            catch (SocketException ex) { return new(0, 0, [], $"无法绑定源地址 {local}：{ex.Message}"); }
            var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
            var rtts = new List<TimeSpan>();
            var buffer = new byte[2048];
            for (ushort seq = 1; seq <= count; seq++)
            {
                var packet = BuildEcho(id, seq);
                var started = DateTime.UtcNow;
                try { await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(peer, 0), ct); }
                catch (SocketException ex) { return new(seq, rtts.Count, rtts.ToArray(), $"发送失败：{ex.Message}"); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(timeout);
                try
                {
                    while (true)
                    {
                        var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None,
                            new IPEndPoint(IPAddress.Any, 0), wait.Token);
                        if (Matches(buffer.AsSpan(0, received.ReceivedBytes), raw, id, seq, ((IPEndPoint)received.RemoteEndPoint).Address, peer))
                        {
                            rtts.Add(DateTime.UtcNow - started);
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (SocketException ex) { return new(seq, rtts.Count, rtts.ToArray(), $"接收失败：{ex.Message}"); }
            }
            return new(count, rtts.Count, rtts.ToArray(), null);
        }
    }

    public static async Task<string> DescribeAsync(IPAddress local, IPAddress peer, int count, TimeSpan timeout, CancellationToken ct)
    {
        var result = await PingAsync(local, peer, count, timeout, ct);
        var text = new StringBuilder($"ICMP 探测 {local} -> {peer}：发送 {result.Sent}，收到 {result.Received}");
        if (result.RoundTrips.Length > 0) text.Append($"，平均 {result.RoundTrips.Average(r => r.TotalMilliseconds):F1} ms");
        if (result.Error is not null) text.Append($"\n错误：{result.Error}");
        return text.ToString();
    }

    internal static byte[] BuildEcho(ushort id, ushort seq)
    {
        var packet = new byte[8 + 32];
        packet[0] = 8;
        packet[4] = (byte)(id >> 8); packet[5] = (byte)id;
        packet[6] = (byte)(seq >> 8); packet[7] = (byte)seq;
        for (var i = 8; i < packet.Length; i++) packet[i] = (byte)i;
        var checksum = Checksum(packet);
        packet[2] = (byte)(checksum >> 8); packet[3] = (byte)checksum;
        return packet;
    }

    internal static bool Matches(ReadOnlySpan<byte> data, bool raw, ushort id, ushort seq, IPAddress from, IPAddress peer)
    {
        if (!from.Equals(peer)) return false;
        // Raw sockets include the IP header; datagram ICMP sockets do not.
        if (raw)
        {
            if (data.Length < 20 || data[0] >> 4 != 4) return false;
            data = data[((data[0] & 0x0f) * 4)..];
        }
        if (data.Length < 8 || data[0] != 0) return false;
        // The kernel rewrites the identifier on datagram sockets, so only the sequence is checked there.
        return (!raw || (data[4] << 8 | data[5]) == id) && (data[6] << 8 | data[7]) == seq;
    }

    private static ushort Checksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) sum += (uint)(data[i] << 8 | data[i + 1]);
        if (data.Length % 2 == 1) sum += (uint)(data[^1] << 8);
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }
}
