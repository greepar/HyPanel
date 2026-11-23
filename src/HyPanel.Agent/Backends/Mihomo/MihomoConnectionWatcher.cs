using System.Net.WebSockets;
using System.Text.Json;

namespace HyPanel.Agent.Backends.Mihomo;

/// <summary>
/// Keeps a WebSocket to one Mihomo instance's <c>/connections</c> stream, which pushes the open connections every
/// <see cref="Interval"/>. Seeing each connection until shortly before it closes leaves only the last fraction of a
/// second to the process-total reconciliation in <see cref="MihomoTrafficTracker"/>, so the split between users stays
/// close to exact. Stops by itself when nobody has asked for the traffic for a while (the service is gone).
/// </summary>
internal sealed class MihomoConnectionWatcher : IDisposable
{
    public const int Interval = 500;
    private const int MaximumMessageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);
    private readonly MihomoTrafficTracker tracker = new();
    private readonly CancellationTokenSource stopping = new();
    private readonly TimeProvider clock;
    private IReadOnlyDictionary<string, Guid> names = new Dictionary<string, Guid>();
    private long lastWanted;
    private long lastMessage;

    public MihomoConnectionWatcher(int port, string secret, TimeProvider clock, Action<MihomoConnectionWatcher> stopped)
    {
        Port = port;
        Secret = secret;
        this.clock = clock;
        lastWanted = clock.GetTimestamp();
        _ = Task.Run(async () =>
        {
            try { await RunAsync(stopping.Token); }
            finally { stopped(this); }
        });
    }

    public int Port { get; }

    public string Secret { get; }

    /// <summary>True while snapshots arrive; otherwise the caller polls once itself.</summary>
    public bool IsStreaming => lastMessage != 0 && clock.GetElapsedTime(Interlocked.Read(ref lastMessage)) < TimeSpan.FromSeconds(5);

    /// <summary>Updates who the login names belong to and keeps the watcher alive.</summary>
    public void Want(IReadOnlyDictionary<string, Guid> loginNames, IReadOnlySet<Guid> users)
    {
        Interlocked.Exchange(ref lastWanted, clock.GetTimestamp());
        lock (tracker)
        {
            names = loginNames;
            tracker.Retain(users);
        }
    }

    /// <summary>Takes in one snapshot (from the stream or a fallback poll), or none, and returns the users' totals.</summary>
    public IReadOnlyDictionary<Guid, (long Upload, long Download)> Observe(MihomoConnections? snapshot)
    {
        lock (tracker)
        {
            var totals = snapshot is null
                ? tracker.Totals
                : tracker.Observe(snapshot, name => name is not null && names.TryGetValue(name, out var id) ? id : null);
            // A copy, so the caller reads it outside the lock while the stream keeps counting.
            return new Dictionary<Guid, (long Upload, long Download)>(totals);
        }
    }

    public void Dispose()
    {
        stopping.Cancel();
        stopping.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (!cancellationToken.IsCancellationRequested
               && clock.GetElapsedTime(Interlocked.Read(ref lastWanted)) < IdleTimeout)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", $"Bearer {Secret}");
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/connections?interval={Interval}"), connect.Token);
                }
                using var message = new MemoryStream();
                while (socket.State == WebSocketState.Open
                       && clock.GetElapsedTime(Interlocked.Read(ref lastWanted)) < IdleTimeout)
                {
                    var result = await socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) break;
                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaximumMessageBytes) break;
                    if (!result.EndOfMessage) continue;
                    MihomoConnections? snapshot = null;
                    try
                    {
                        snapshot = JsonSerializer.Deserialize(message.GetBuffer().AsSpan(0, (int)message.Length),
                            MihomoApiJsonSerializerContext.Default.MihomoConnections);
                    }
                    catch (JsonException)
                    {
                    }
                    message.SetLength(0);
                    if (snapshot is null) continue;
                    Observe(snapshot);
                    Interlocked.Exchange(ref lastMessage, clock.GetTimestamp());
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException
                                                  or HttpRequestException or IOException)
            {
                // Mihomo is (re)starting; try again shortly.
            }
            try { await Task.Delay(ReconnectDelay, clock, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
