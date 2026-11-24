using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;

namespace HyPanel.Agent;

/// <summary>
/// Runs the proxy tests the Panel asks for: each looks up this node's address through the proxy, as a service sending
/// its traffic through it would, and reports what the lookup host saw. Tests run in the background; a finished one
/// wakes the sync loop so the admin sees the result within a second or two.
/// </summary>
public sealed class ProxyTester(ILogger<ProxyTester> logger)
{
    private const int MaxResponseBytes = 8192;
    private static readonly Uri LookupUri = new("https://4.qwq.lu/?detail", UriKind.Absolute);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly ConcurrentDictionary<Guid, ProxyTestReport?> tests = new();

    /// <summary>Lookup override for tests.</summary>
    internal Func<UpstreamProxy, CancellationToken, Task<(string Ip, string? Country, string? City)>>? Lookup { get; init; }

    /// <summary>
    /// Starts the tests not seen before and forgets the ones the Panel no longer lists. <paramref name="finished"/> is
    /// called when a test completes.
    /// </summary>
    public void Apply(IReadOnlyList<ProxyTestRequest> requests, Action finished)
    {
        var listed = requests.Select(static request => request.TestId).ToHashSet();
        foreach (var id in tests.Keys)
            if (!listed.Contains(id)) tests.TryRemove(id, out _);
        foreach (var request in requests)
        {
            if (!tests.TryAdd(request.TestId, null)) continue;
            _ = Task.Run(async () =>
            {
                var report = await RunAsync(request);
                if (tests.ContainsKey(request.TestId)) tests[request.TestId] = report;
                finished();
            });
        }
    }

    /// <summary>The finished tests the Panel is still waiting on.</summary>
    public IReadOnlyList<ProxyTestReport> Reports() => [.. tests.Values.OfType<ProxyTestReport>()];

    internal async Task<ProxyTestReport> RunAsync(ProxyTestRequest request)
    {
        if (!UpstreamProxy.TryCreate(request.Type, request.Host, request.Port, request.Username, request.Password,
                out var proxy, out var error) || proxy is null)
            return Failed(request, error ?? "代理配置不完整。");
        var watch = Stopwatch.StartNew();
        try
        {
            using var timeout = new CancellationTokenSource(Timeout);
            var (ip, country, city) = await (Lookup ?? LookupAsync)(proxy, timeout.Token);
            return new ProxyTestReport(request.TestId, true, ip, country, city, (int)watch.ElapsedMilliseconds, null);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Proxy test through {Host} failed.", proxy.Host);
            return Failed(request, Describe(exception));
        }
    }

    private static ProxyTestReport Failed(ProxyTestRequest request, string error) =>
        new(request.TestId, false, null, null, null, null, error);

    /// <summary>What went wrong, in terms the admin can act on.</summary>
    internal static string Describe(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OperationCanceledException or TimeoutException:
                    return $"{Timeout.TotalSeconds:0} 秒内没有响应，代理可能不通或太慢。";
                case HttpRequestException { StatusCode: HttpStatusCode.ProxyAuthenticationRequired }:
                    return "代理拒绝了用户名或密码。";
                case HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError }:
                    return "代理拒绝了连接，可能是用户名密码不对，或没有把这个节点的 IP 加入白名单。";
                case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError }:
                    return "解析不了代理地址。";
                case SocketException { SocketErrorCode: SocketError.ConnectionRefused }:
                    return "代理端口拒绝连接，请检查地址和端口。";
                case SocketException { SocketErrorCode: SocketError.TimedOut or SocketError.HostUnreachable or SocketError.NetworkUnreachable }:
                    return "连不上代理，请检查地址和端口。";
                case AuthenticationException:
                    return "和代理建立 TLS 失败，代理可能不是 HTTPS 类型。";
                case InvalidDataException or JsonException:
                    return "通过代理拿到的回应不对，代理可能拦截或改写了请求。";
            }
        }
        return exception.Message;
    }

    private static async Task<(string Ip, string? Country, string? City)> LookupAsync(UpstreamProxy proxy,
        CancellationToken cancellationToken)
    {
        var address = new Uri(proxy.ToUrl());
        var webProxy = new WebProxy(new UriBuilder(address) { UserName = "", Password = "" }.Uri);
        if (proxy.Username is not null) webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? "");
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = webProxy, UseProxy = true, AllowAutoRedirect = false });
        using var response = await client.GetAsync(LookupUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw new InvalidDataException();
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (body.Length > MaxResponseBytes) throw new InvalidDataException();
        var result = PublicIpResolver.Parse(body);
        return IPAddress.TryParse(result.Ip, out _) ? result : throw new InvalidDataException();
    }
}
