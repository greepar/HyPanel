namespace HyPanel.Agent;

using System.Net;
using System.Text.Json;

/// <summary>Looks up this host's public IPv4 and its country (ISO code, shown as a flag in the Panel).</summary>
public sealed class PublicIpv4Resolver(HttpClient httpClient, TimeProvider timeProvider)
{
    private const int MaxResponseBytes = 8192;
    private static readonly Uri LookupUri = new("https://4.qwq.lu/?detail", UriKind.Absolute);
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cached;
    private string? cachedCountry;

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

            using var request = new HttpRequestMessage(HttpMethod.Get, LookupUri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
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
                var (value, country) = Parse(buffer.AsSpan(0, read));
                if (!IPAddress.TryParse(value, out var address) ||
                    address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    !string.Equals(value, address.ToString(), StringComparison.Ordinal))
                    throw new InvalidDataException();
                cached = value;
                cachedCountry = country;
                refreshAt = now + SuccessLifetime;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or JsonException or
                                                   OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                refreshAt = now + FailureLifetime;
            }

            return cached;
        }
        finally
        {
            gate.Release();
        }
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
