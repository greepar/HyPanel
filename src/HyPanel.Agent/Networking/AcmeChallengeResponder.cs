using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyPanel.Shared.Contracts;

namespace HyPanel.Agent.Networking;

/// <summary>
/// Proves ACME challenges for certificates the Panel obtains: <c>http-01</c> on port 80 and <c>tls-alpn-01</c> on TCP
/// 443 (RFC 8737). Listeners run only while the Panel lists a challenge of their kind, so the ports are free the rest
/// of the time. A port that is already taken is reported with the program holding it.
/// </summary>
public sealed class AcmeChallengeResponder(int httpPort = 80, int tlsPort = 443) : IDisposable
{
    public const string Http01 = "http-01";
    public const string TlsAlpn01 = "tls-alpn-01";
    private const string ChallengePath = "/.well-known/acme-challenge/";
    private static readonly SslApplicationProtocol AcmeTls = new("acme-tls/1");
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(10);
    private readonly object gate = new();
    private Dictionary<string, AcmeChallengeAssignment> http = new(StringComparer.Ordinal);
    private Dictionary<string, X509Certificate2> tls = new(StringComparer.OrdinalIgnoreCase);
    private List<AcmeChallengeAssignment> assigned = [];
    private Listener? httpListener;
    private Listener? tlsListener;
    private string? httpError;
    private string? tlsError;

    /// <summary>The ports a firewall must let in while challenges are listed.</summary>
    public IReadOnlyCollection<FirewallOpening> Openings
    {
        get
        {
            lock (gate)
                return [.. (httpListener is null ? [] : new[] { new FirewallOpening("tcp", httpPort, null) })
                    .Concat(tlsListener is null ? [] : [new FirewallOpening("tcp", tlsPort, null)])];
        }
    }

    /// <summary>Takes the Panel's full list of challenges, starting or stopping listeners as needed.</summary>
    public void Apply(IReadOnlyList<AcmeChallengeAssignment> challenges)
    {
        lock (gate)
        {
            var valid = challenges.Where(IsValid).ToList();
            assigned = valid;
            http = valid.Where(item => item.Type == Http01).GroupBy(item => item.Token)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var nextTls = new Dictionary<string, X509Certificate2>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in valid.Where(item => item.Type == TlsAlpn01))
                nextTls[item.Domain] = tls.Remove(item.Domain, out var existing) && SameChallenge(existing, item)
                    ? existing
                    : ChallengeCertificate(item.Domain, item.KeyAuthorization);
            foreach (var stale in tls.Values) stale.Dispose();
            tls = nextTls;

            (httpListener, httpError) = Reconcile(httpListener, http.Count > 0, httpPort, ServeHttpAsync);
            (tlsListener, tlsError) = Reconcile(tlsListener, tls.Count > 0, tlsPort, ServeTlsAsync);
        }
    }

    /// <summary>What to tell the Panel about every listed challenge.</summary>
    public IReadOnlyList<AcmeChallengeReport> Reports()
    {
        lock (gate)
            return [.. assigned.Select(item =>
            {
                var error = item.Type == Http01 ? httpError : tlsError;
                return new AcmeChallengeReport(item.Token, error is null, error);
            })];
    }

    public void Dispose()
    {
        lock (gate)
        {
            httpListener?.Dispose();
            tlsListener?.Dispose();
            httpListener = tlsListener = null;
            foreach (var certificate in tls.Values) certificate.Dispose();
            tls.Clear();
        }
    }

    private static bool IsValid(AcmeChallengeAssignment item) =>
        item.Type is Http01 or TlsAlpn01 && item.Token is { Length: > 0 and <= 256 }
        && item.Token.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
        && item.KeyAuthorization.StartsWith(item.Token + ".", StringComparison.Ordinal) && item.KeyAuthorization.Length <= 512
        && Uri.CheckHostName(item.Domain) == UriHostNameType.Dns && !item.Domain.Contains('*');

    private static (Listener?, string?) Reconcile(Listener? current, bool wanted, int port,
        Func<Socket, CancellationToken, Task> serve)
    {
        if (!wanted)
        {
            current?.Dispose();
            return (null, null);
        }
        if (current is not null) return (current, null);
        try
        {
            return (Listener.Start(port, serve), null);
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return (null, PortProbe.FindHolder("tcp", port).Describe() + "，无法完成证书验证");
        }
        catch (SocketException exception)
        {
            return (null, $"无法监听 TCP {port}：{exception.Message}");
        }
    }

    private async Task ServeHttpAsync(Socket socket, CancellationToken cancellationToken)
    {
        await using var stream = new NetworkStream(socket, ownsSocket: true);
        var buffer = new byte[4096];
        var length = 0;
        // Only the request line matters; read until the end of the headers or a full buffer.
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0) break;
            length += read;
            if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
        }
        var requestLine = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", 2)[0].Split(' ');
        string? body = null;
        if (requestLine is ["GET", var target, ..] && target.StartsWith(ChallengePath, StringComparison.Ordinal))
            lock (gate)
                body = http.TryGetValue(target[ChallengePath.Length..], out var item) ? item.KeyAuthorization : null;
        var content = Encoding.ASCII.GetBytes(body ?? "not found");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(body is null ? "404 Not Found" : "200 OK")}\r\nContent-Type: text/plain\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(content, cancellationToken);
    }

    private async Task ServeTlsAsync(Socket socket, CancellationToken cancellationToken)
    {
        await using var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ApplicationProtocols = [AcmeTls],
            ServerCertificateSelectionCallback = (_, name) =>
            {
                lock (gate) return name is not null && tls.TryGetValue(name, out var certificate) ? certificate : null!;
            }
        }, cancellationToken);
        // The validation is the handshake itself; nothing is exchanged afterwards.
    }

    /// <summary>
    /// The RFC 8737 certificate: the domain as its only name and the critical acmeIdentifier extension carrying the
    /// SHA-256 of the key authorization.
    /// </summary>
    internal static X509Certificate2 ChallengeCertificate(string domain, string keyAuthorization)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={domain}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(domain);
        request.CertificateExtensions.Add(names.Build());
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.WriteOctetString(SHA256.HashData(Encoding.ASCII.GetBytes(keyAuthorization)));
        request.CertificateExtensions.Add(new X509Extension("1.3.6.1.5.5.7.1.31", writer.Encode(), critical: true));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7));
        // A round trip through PKCS#12 gives a key every platform's TLS stack accepts for a server certificate.
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    private static bool SameChallenge(X509Certificate2 certificate, AcmeChallengeAssignment item)
    {
        var extension = certificate.Extensions["1.3.6.1.5.5.7.1.31"];
        if (extension is null) return false;
        var expected = SHA256.HashData(Encoding.ASCII.GetBytes(item.KeyAuthorization));
        return new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadOctetString().AsSpan().SequenceEqual(expected);
    }

    /// <summary>One TCP listener (both address families when possible) serving connections until disposed.</summary>
    private sealed class Listener : IDisposable
    {
        private readonly CancellationTokenSource stopping = new();
        private readonly Socket socket;

        private Listener(Socket socket, Func<Socket, CancellationToken, Task> serve)
        {
            this.socket = socket;
            _ = Task.Run(() => AcceptAsync(serve));
        }

        public static Listener Start(int port, Func<Socket, CancellationToken, Task> serve)
        {
            Socket socket;
            if (Socket.OSSupportsIPv6)
            {
                socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
                try { socket.Bind(new IPEndPoint(IPAddress.IPv6Any, port)); }
                catch (SocketException exception) when (exception.SocketErrorCode != SocketError.AddressAlreadyInUse)
                {
                    socket.Dispose();
                    socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    socket.Bind(new IPEndPoint(IPAddress.Any, port));
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
            else
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try { socket.Bind(new IPEndPoint(IPAddress.Any, port)); }
                catch { socket.Dispose(); throw; }
            }
            socket.Listen(64);
            return new Listener(socket, serve);
        }

        public void Dispose()
        {
            stopping.Cancel();
            socket.Dispose();
        }

        private async Task AcceptAsync(Func<Socket, CancellationToken, Task> serve)
        {
            while (!stopping.IsCancellationRequested)
            {
                Socket client;
                try { client = await socket.AcceptAsync(stopping.Token); }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException
                                                      or SocketException)
                {
                    if (stopping.IsCancellationRequested) return;
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping.Token);
                    timeout.CancelAfter(ConnectionTimeout);
                    try { await serve(client, timeout.Token); }
                    catch (Exception exception) when (exception is IOException or OperationCanceledException
                                                          or SocketException or AuthenticationException
                                                          or ObjectDisposedException)
                    {
                        // A probe or an aborted handshake; the validator retries if it needs to.
                    }
                    finally { client.Dispose(); }
                });
            }
        }
    }
}
