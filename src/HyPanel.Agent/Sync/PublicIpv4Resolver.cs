namespace HyPanel.Agent;

using System.Net;

public sealed class PublicIpv4Resolver(HttpClient httpClient, TimeProvider timeProvider)
{
    private static readonly Uri LookupUri = new("https://4.qwq.lu", UriKind.Absolute);
    private static readonly TimeSpan SuccessLifetime = TimeSpan.FromHours(6);
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cached;
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
                if (response.Content.Headers.ContentLength is > 64) throw new InvalidDataException();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[65];
                var read = 0;
                while (read < buffer.Length)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);
                    if (count == 0) break;
                    read += count;
                }
                if (read == buffer.Length) throw new InvalidDataException();
                var value = System.Text.Encoding.ASCII.GetString(buffer, 0, read).Trim();
                if (!IPAddress.TryParse(value, out var address) ||
                    address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                    !string.Equals(value, address.ToString(), StringComparison.Ordinal))
                    throw new InvalidDataException();
                cached = value;
                refreshAt = now + SuccessLifetime;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or
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
}
