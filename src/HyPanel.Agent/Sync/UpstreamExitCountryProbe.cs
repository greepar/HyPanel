namespace HyPanel.Agent;

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;

/// <summary>
/// Finds the country a service's traffic leaves from when it goes out through an upstream proxy, so subscriptions can
/// flag the proxy's country instead of the node's. Lookups run in the background through the proxy itself; callers
/// get whatever is known right now and never wait on a slow proxy.
/// </summary>
public sealed class UpstreamExitCountryProbe(ILogger<UpstreamExitCountryProbe> logger, TimeProvider timeProvider)
{
    private const int MaxResponseBytes = 8192;
    private static readonly Uri LookupUri = new("https://4.qwq.lu/?detail", UriKind.Absolute);
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>Lookup override for tests: the proxy and a token, returning the country or null.</summary>
    internal Func<UpstreamProxy, CancellationToken, Task<string?>>? Lookup { get; init; }

    private sealed class Entry
    {
        public string? Country;
        public DateTimeOffset RefreshAt;
        public int Running;
    }

    /// <summary>Exit country per service that has an upstream proxy and a known country.</summary>
    public IReadOnlyDictionary<Guid, string> Get(IEnumerable<ServiceDesiredState> services)
    {
        var now = timeProvider.GetUtcNow();
        var countries = new Dictionary<Guid, string>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            if (!service.Enabled || ProxyOf(service.ConfigJson) is not { } proxy) continue;
            var key = proxy.ToUrl();
            live.Add(key);
            var entry = entries.GetOrAdd(key, _ => new Entry());
            if (now >= entry.RefreshAt && Interlocked.Exchange(ref entry.Running, 1) == 0)
                _ = RefreshAsync(proxy, entry);
            if (Volatile.Read(ref entry.Country) is { } country) countries[service.ServiceId] = country;
        }
        foreach (var key in entries.Keys)
            if (!live.Contains(key)) entries.TryRemove(key, out _);
        return countries;
    }

    private async Task RefreshAsync(UpstreamProxy proxy, Entry entry)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);
            var country = await (Lookup ?? LookupAsync)(proxy, timeout.Token);
            if (country is not null) Volatile.Write(ref entry.Country, country);
            entry.RefreshAt = timeProvider.GetUtcNow() + (country is null ? FailureLifetime : SuccessLifetime);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Could not look up the exit country of upstream proxy {Host}.", proxy.Host);
            entry.RefreshAt = timeProvider.GetUtcNow() + FailureLifetime;
        }
        finally
        {
            Volatile.Write(ref entry.Running, 0);
        }
    }

    private static async Task<string?> LookupAsync(UpstreamProxy proxy, CancellationToken cancellationToken)
    {
        var address = new Uri(proxy.ToUrl());
        var webProxy = new WebProxy(new UriBuilder(address) { UserName = "", Password = "" }.Uri);
        if (proxy.Username is not null) webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password ?? "");
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = webProxy, UseProxy = true, AllowAutoRedirect = false });
        using var response = await client.GetAsync(LookupUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) return null;
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return body.Length > MaxResponseBytes ? null : ParseCountry(body);
    }

    /// <summary>The upper-case ISO country of a <c>{"country":"GB",…}</c> body, or null.</summary>
    internal static string? ParseCountry(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("country", out var value) &&
                   value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 2 } code &&
                   char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1])
                ? code.ToUpperInvariant()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The upstream proxy in a service config (the same flat fields for every backend), or null.</summary>
    internal static UpstreamProxy? ProxyOf(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            int? port = root.TryGetProperty("upstreamProxyPort", out var portValue) && portValue.TryGetInt32(out var number)
                ? number : null;
            return UpstreamProxy.TryCreate(Text("upstreamProxyType"), Text("upstreamProxyHost"), port,
                Text("upstreamProxyUsername"), Text("upstreamProxyPassword"), out var proxy, out _) ? proxy : null;
        }
        catch (JsonException) { return null; }
    }
}
