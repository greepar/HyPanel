namespace HyPanel.Agent;

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

/// <summary>Looks up this host's public IPv4 and its country (ISO code, shown as a flag in the Panel).</summary>
public sealed class PublicIpv4Resolver(HttpClient httpClient, TimeProvider timeProvider)
    : PublicIpResolver(timeProvider, forgetAfter: null)
{
    private static readonly Uri LookupUri = new("https://4.qwq.lu/?detail", UriKind.Absolute);

    protected override async Task<(string Ip, string? Country)> LookupAsync(CancellationToken cancellationToken)
    {
        var result = await FetchAsync(httpClient, LookupUri, cancellationToken);
        return IPAddress.TryParse(result.Ip, out var address) && address.AddressFamily == AddressFamily.InterNetwork &&
               string.Equals(result.Ip, address.ToString(), StringComparison.Ordinal)
            ? result
            : throw new InvalidDataException();
    }
}

/// <summary>
/// Looks up this host's public IPv6, offered to clients as a second entry for each service. Each global address on
/// the host is tried as the source, and only an answer naming that same address counts: outgoing IPv6 often leaves
/// through a tunnel such as Cloudflare WARP, whose exit address cannot be dialled back. An address that keeps
/// failing for a day is forgotten so the Panel stops handing out an entry that no longer works.
/// </summary>
public sealed class PublicIpv6Resolver : PublicIpResolver
{
    private const int MaximumCandidates = 8;
    private static readonly Uri LookupUri = new("https://6.qwq.lu/?detail", UriKind.Absolute);
    private readonly Func<IReadOnlyList<IPAddress>> localAddresses;
    private readonly Func<IPAddress, HttpClient> clientFor;

    public PublicIpv6Resolver(TimeProvider timeProvider)
        : this(timeProvider, LocalGlobalAddresses, BoundClient)
    {
    }

    internal PublicIpv6Resolver(TimeProvider timeProvider, Func<IReadOnlyList<IPAddress>> localAddresses,
        Func<IPAddress, HttpClient> clientFor) : base(timeProvider, forgetAfter: TimeSpan.FromDays(1))
    {
        this.localAddresses = localAddresses;
        this.clientFor = clientFor;
    }

    protected override async Task<(string Ip, string? Country)> LookupAsync(CancellationToken cancellationToken)
    {
        foreach (var source in localAddresses().Where(IsGlobalUnicast).Take(MaximumCandidates))
        {
            try
            {
                using var client = clientFor(source);
                var result = await FetchAsync(client, LookupUri, cancellationToken);
                if (string.Equals(result.Ip, source.ToString(), StringComparison.Ordinal)) return result;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
                                                  JsonException or SocketException ||
                                              exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // This source cannot reach the lookup host; try the next one.
            }
        }
        throw new InvalidDataException();
    }

    private static bool IsGlobalUnicast(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xE0) == 0x20;

    private static IReadOnlyList<IPAddress> LocalGlobalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
            .SelectMany(networkInterface => networkInterface.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(IsGlobalUnicast)
            .Distinct()
            .ToArray();

    /// <summary>A client whose connections leave from <paramref name="source"/>, so routing rules keyed on it apply.</summary>
    private static HttpClient BoundClient(IPAddress source) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                socket.Bind(new IPEndPoint(source, 0));
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    });
}

public abstract class PublicIpResolver(TimeProvider timeProvider, TimeSpan? forgetAfter)
{
    private const int MaxResponseBytes = 8192;
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cached;
    private string? cachedCountry;
    private DateTimeOffset succeededAt;

    /// <summary>Country from the last successful lookup; call <see cref="GetAsync"/> first.</summary>
    public string? CountryCode => cachedCountry;
    private DateTimeOffset refreshAt;

    public async Task<string?> GetAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (now < refreshAt) return cached;

        await gate.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (now < refreshAt) return cached;

            try
            {
                var (value, country) = await LookupAsync(cancellationToken);
                cached = value;
                cachedCountry = country;
                succeededAt = now;
                refreshAt = now + SuccessLifetime;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or
                                                   OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                refreshAt = now + FailureLifetime;
                if (forgetAfter is { } limit && now - succeededAt >= limit) cached = cachedCountry = null;
            }

            return cached;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The address and country seen by the lookup host; throws when it cannot be used.</summary>
    protected abstract Task<(string Ip, string? Country)> LookupAsync(CancellationToken cancellationToken);

    protected static async Task<(string Ip, string? Country)> FetchAsync(HttpClient client, Uri uri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxResponseBytes) throw new InvalidDataException();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[MaxResponseBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
            if (count == 0) break;
            read += count;
        }
        if (read == buffer.Length) throw new InvalidDataException();
        return Parse(buffer.AsSpan(0, read));
    }

    /// <summary>Reads <c>{"ip":"…","country":"GB",…}</c>; a missing or malformed country is ignored.</summary>
    private static (string Ip, string? Country) Parse(ReadOnlySpan<byte> body)
    {
        var reader = new Utf8JsonReader(body);
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ip", out var ip) ||
            ip.ValueKind != JsonValueKind.String) throw new InvalidDataException();
        string? country = null;
        if (root.TryGetProperty("country", out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { Length: 2 } code && char.IsAsciiLetter(code[0]) && char.IsAsciiLetter(code[1]))
            country = code.ToUpperInvariant();
        return (ip.GetString() ?? string.Empty, country);
    }
}
