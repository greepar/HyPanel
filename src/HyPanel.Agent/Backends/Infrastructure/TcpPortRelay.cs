namespace HyPanel.Agent.Backends.Infrastructure;

using System.Buffers;
using System.Net;
using System.Net.Sockets;
using HyPanel.Shared.Contracts;

/// <summary>
/// Forwards TCP ports to the node that really runs a service, in user space and without any system component.
/// The relayed stream is whatever the service speaks (REALITY is end-to-end encrypted), so the relay only moves
/// bytes. Each rule owns a listening socket; changed or removed rules are closed, established connections of a
/// removed rule are closed with it.
/// </summary>
public sealed class TcpPortRelay(ILogger<TcpPortRelay> logger) : IDisposable
{
    /// <summary>Upper bound on relayed ports: each one holds a listening socket.</summary>
    public const int MaximumPorts = 1024;
    private const int BufferSize = 32 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly Lock gate = new();
    private readonly Dictionary<int, Listener> listeners = new();
    private readonly Dictionary<int, string> failures = new();

    public int ListenerCount
    {
        get { lock (gate) return listeners.Count; }
    }

    /// <summary>
    /// Makes the relayed ports exactly <paramref name="rules"/>. Returns the listen ports that could not be bound
    /// with the reason; bind failures are retried by the next call.
    /// </summary>
    public IReadOnlyDictionary<int, string> Apply(IReadOnlyList<PortRelayRule> rules)
    {
        var wanted = rules.Where(rule => rule.ListenPort is >= 1 and <= 65535 && rule.TargetPort is >= 1 and <= 65535
                && rule.TargetHost.Length > 0)
            .GroupBy(rule => rule.ListenPort).Select(group => group.First()).Take(MaximumPorts)
            .ToDictionary(rule => rule.ListenPort);
        lock (gate)
        {
            foreach (var (port, listener) in listeners.ToArray())
                if (!wanted.TryGetValue(port, out var rule) || rule.TargetHost != listener.Host || rule.TargetPort != listener.TargetPort)
                {
                    listener.Dispose();
                    listeners.Remove(port);
                    logger.LogInformation("TCP relay on port {Port} stopped.", port);
                }
            foreach (var port in failures.Keys.Where(port => !wanted.ContainsKey(port)).ToArray()) failures.Remove(port);
            foreach (var (port, rule) in wanted)
            {
                if (listeners.ContainsKey(port)) continue;
                try
                {
                    var listener = new Listener(rule, logger);
                    listeners[port] = listener;
                    listener.Start();
                    failures.Remove(port);
                    logger.LogInformation("TCP relay: port {Port} -> {Host}:{Target}.", port, rule.TargetHost, rule.TargetPort);
                }
                catch (SocketException exception)
                {
                    listeners.Remove(port);
                    var reason = exception.SocketErrorCode == SocketError.AddressAlreadyInUse
                        ? $"端口 {port} 已被占用" : $"无法监听端口 {port}：{exception.SocketErrorCode}";
                    if (!failures.TryGetValue(port, out var previous) || previous != reason)
                        logger.LogWarning("Could not relay TCP port {Port}: {Error}", port, exception.SocketErrorCode);
                    failures[port] = reason;
                }
            }
            return new Dictionary<int, string>(failures);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var listener in listeners.Values) listener.Dispose();
            listeners.Clear();
        }
    }

    private sealed class Listener : IDisposable
    {
        private readonly Socket socket;
        private readonly CancellationTokenSource cancellation = new();
        private readonly ILogger logger;

        public Listener(PortRelayRule rule, ILogger logger)
        {
            Host = rule.TargetHost;
            TargetPort = rule.TargetPort;
            this.logger = logger;
            socket = Bind(rule.ListenPort);
        }

        public string Host { get; }
        public int TargetPort { get; }

        public void Start() => _ = Task.Run(AcceptLoopAsync);

        private static Socket Bind(int port)
        {
            Socket socket;
            try
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch (SocketException exception) when (exception.SocketErrorCode != SocketError.AddressAlreadyInUse)
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            socket.Listen(512);
            return socket;
        }

        private async Task AcceptLoopAsync()
        {
            var token = cancellation.Token;
            while (!token.IsCancellationRequested)
            {
                Socket client;
                try { client = await socket.AcceptAsync(token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { await Task.Delay(100, CancellationToken.None); continue; }
                _ = Task.Run(() => RelayAsync(client, token));
            }
        }

        private async Task RelayAsync(Socket client, CancellationToken token)
        {
            using var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using (client)
                {
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connect.CancelAfter(ConnectTimeout);
                    var addresses = await Dns.GetHostAddressesAsync(Host, AddressFamily.InterNetwork, connect.Token);
                    await upstream.ConnectAsync(addresses, TargetPort, connect.Token);
                    foreach (var socket in new[] { client, upstream })
                    {
                        socket.NoDelay = true;
                        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                    }
                    await Task.WhenAll(PumpAsync(client, upstream, token), PumpAsync(upstream, client, token));
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException
                or ObjectDisposedException or IOException)
            {
                logger.LogDebug("TCP relay connection ended: {Reason}", exception.Message);
            }
        }

        /// <summary>Copies one direction; on EOF or error the peer's send side is closed so the other direction drains.</summary>
        private static async Task PumpAsync(Socket from, Socket to, CancellationToken token)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                while (true)
                {
                    var read = await from.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, token);
                    if (read == 0) break;
                    var sent = 0;
                    while (sent < read) sent += await to.SendAsync(buffer.AsMemory(sent, read - sent), SocketFlags.None, token);
                }
                try { to.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or IOException)
            {
                // A reset on either side ends the whole connection.
                try { from.Dispose(); } catch (ObjectDisposedException) { }
                try { to.Dispose(); } catch (ObjectDisposedException) { }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            socket.Dispose();
            cancellation.Dispose();
        }
    }
}
