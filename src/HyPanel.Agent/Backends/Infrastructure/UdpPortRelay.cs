namespace HyPanel.Agent.Backends.Infrastructure;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

/// <summary>
/// Port hopping without nftables, for Agents in the Linux VM of Docker Desktop or OrbStack: those forward only
/// ports something listens on, and their forwarders use connected sockets that never see replies rewritten by a
/// NAT redirect. So every hop port gets a real socket, and each client (per hop port) is relayed through its own
/// connected socket to the service's listen port on loopback; replies go back out of the hop port they came in on.
/// </summary>
public sealed class UdpPortRelay(ILogger<UdpPortRelay> logger) : IDisposable
{
    /// <summary>Upper bound on relayed ports: each one holds a socket and a receive buffer.</summary>
    public const int MaximumPorts = 1024;
    private const int BufferSize = 16 * 1024;
    private static readonly TimeSpan SessionIdleTimeout = TimeSpan.FromMinutes(2);

    private readonly Lock gate = new();
    private readonly Dictionary<int, Listener> listeners = new();
    private Timer? sweeper;

    public int ListenerCount
    {
        get { lock (gate) return listeners.Count; }
    }

    /// <summary>
    /// Makes the relayed ports exactly <paramref name="rules"/> (hop port -> listen port). Returns the ports that
    /// could not be bound; the rest are relayed.
    /// </summary>
    public IReadOnlyList<int> Apply(IReadOnlyDictionary<int, int> rules)
    {
        var failed = new List<int>();
        lock (gate)
        {
            foreach (var (port, listener) in listeners.ToArray())
                if (!rules.TryGetValue(port, out var target) || target != listener.TargetPort)
                {
                    listener.Dispose();
                    listeners.Remove(port);
                }
            foreach (var (port, target) in rules)
            {
                if (listeners.ContainsKey(port)) continue;
                try
                {
                    var listener = new Listener(port, target, logger);
                    listeners[port] = listener;
                    listener.Start();
                }
                catch (SocketException exception)
                {
                    logger.LogWarning("Could not relay UDP port {Port}: {Error}", port, exception.SocketErrorCode);
                    failed.Add(port);
                }
            }
            if (listeners.Count > 0)
                sweeper ??= new Timer(_ => Sweep(), null, SessionIdleTimeout, SessionIdleTimeout);
            else
            {
                sweeper?.Dispose();
                sweeper = null;
            }
        }
        return failed;
    }

    private void Sweep()
    {
        Listener[] snapshot;
        lock (gate) snapshot = listeners.Values.ToArray();
        var cutoff = Environment.TickCount64 - (long)SessionIdleTimeout.TotalMilliseconds;
        foreach (var listener in snapshot) listener.CloseIdleSessions(cutoff);
    }

    public void Dispose() => Apply(new Dictionary<int, int>());

    private sealed class Listener : IDisposable
    {
        private readonly Socket socket;
        private readonly ILogger logger;
        private readonly CancellationTokenSource stopping = new();
        private readonly ConcurrentDictionary<IPEndPoint, Session> sessions = new();

        public Listener(int port, int targetPort, ILogger logger)
        {
            TargetPort = targetPort;
            this.logger = logger;
            // IPv4 only: OrbStack does not forward to dual-stack ([::]) sockets.
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        public int TargetPort { get; }

        public void Start() => _ = ReceiveAsync();

        private async Task ReceiveAsync()
        {
            var buffer = new byte[BufferSize];
            EndPoint any = new IPEndPoint(IPAddress.Any, 0);
            while (!stopping.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, stopping.Token);
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }
                catch (SocketException)
                {
                    // ICMP errors from earlier sends surface here on some platforms; keep serving.
                    continue;
                }
                var client = (IPEndPoint)received.RemoteEndPoint;
                try
                {
                    var session = sessions.GetOrAdd(client, static (key, state) => state.Open(key), this);
                    session.Touch();
                    await session.Upstream.SendAsync(buffer.AsMemory(0, received.ReceivedBytes), SocketFlags.None,
                        stopping.Token);
                }
                catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
                {
                    if (sessions.TryRemove(client, out var broken)) broken.Dispose();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        private Session Open(IPEndPoint client)
        {
            var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            upstream.Connect(new IPEndPoint(IPAddress.Loopback, TargetPort));
            var session = new Session(upstream);
            _ = RelayRepliesAsync(client, session);
            return session;
        }

        private async Task RelayRepliesAsync(IPEndPoint client, Session session)
        {
            var buffer = new byte[BufferSize];
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    var length = await session.Upstream.ReceiveAsync(buffer, SocketFlags.None, stopping.Token);
                    session.Touch();
                    await socket.SendToAsync(buffer.AsMemory(0, length), SocketFlags.None, client, stopping.Token);
                }
                catch (SocketException exception) when (exception.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    // The service is not listening (yet); keep the session so the next datagram retries.
                }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
                                                      or SocketException)
                {
                    if (exception is SocketException socketException)
                        logger.LogDebug("UDP relay session for {Client} ended: {Error}", client,
                            socketException.SocketErrorCode);
                    return;
                }
            }
        }

        public void CloseIdleSessions(long cutoff)
        {
            foreach (var (client, session) in sessions)
                if (session.LastActivity < cutoff && sessions.TryRemove(client, out _))
                    session.Dispose();
        }

        public void Dispose()
        {
            stopping.Cancel();
            socket.Dispose();
            foreach (var session in sessions.Values) session.Dispose();
            sessions.Clear();
            // stopping is not disposed: receive loops that are still unwinding may read its token.
        }
    }

    private sealed class Session(Socket upstream) : IDisposable
    {
        private long lastActivity = Environment.TickCount64;
        public Socket Upstream { get; } = upstream;
        public long LastActivity => Interlocked.Read(ref lastActivity);
        public void Touch() => Interlocked.Exchange(ref lastActivity, Environment.TickCount64);
        public void Dispose() => Upstream.Dispose();
    }
}
