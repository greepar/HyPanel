using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyPanel.Server.Acme;

/// <summary>Publishes the TXT record of a <c>dns-01</c> challenge and removes it afterwards.</summary>
internal interface IAcmeDnsPublisher
{
    /// <summary>Creates the record and returns what <see cref="RemoveAsync"/> needs to delete it.</summary>
    Task<string> PublishAsync(string name, string value, CancellationToken ct);

    Task RemoveAsync(string handle, CancellationToken ct);

    /// <summary>Waits until resolvers see the value, so the CA does not look too early.</summary>
    Task WaitVisibleAsync(string name, string value, CancellationToken ct);
}

/// <summary>
/// TXT records through the Cloudflare API with a token allowed to edit the zone's DNS. The zone is found by trying
/// the record's parent names, so the token needs no account-wide access.
/// </summary>
internal sealed class CloudflareDns(HttpClient http, string token) : IAcmeDnsPublisher
{
    private static readonly Uri Api = new("https://api.cloudflare.com/client/v4/");

    public async Task<string> PublishAsync(string name, string value, CancellationToken ct)
    {
        var zone = await ZoneOfAsync(name, ct);
        var record = await SendAsync(HttpMethod.Post, $"zones/{zone}/dns_records", new JsonObject
        {
            ["type"] = "TXT", ["name"] = name, ["content"] = $"\"{value}\"", ["ttl"] = 60,
            ["comment"] = "HyPanel ACME challenge"
        }, ct);
        var id = (string?)record?["id"] ?? throw new AcmeException("Cloudflare 没有返回 DNS 记录 ID。");
        return $"{zone}/{id}";
    }

    public async Task RemoveAsync(string handle, CancellationToken ct)
    {
        var parts = handle.Split('/');
        if (parts.Length != 2) return;
        await SendAsync(HttpMethod.Delete, $"zones/{parts[0]}/dns_records/{parts[1]}", null, ct);
    }

    /// <summary>Asks Cloudflare's own resolver (DNS over HTTPS) until it returns the value, for up to two minutes.</summary>
    public async Task WaitVisibleAsync(string name, string value, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(name)}&type=TXT");
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response = await http.SendAsync(request, ct);
                var answers = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["Answer"] as JsonArray;
                if (answers?.Any(answer => ((string?)answer?["data"])?.Trim('"') == value) == true) return;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException)
            {
                // Try again; the CA has its own retries too.
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    private async Task<string> ZoneOfAsync(string name, CancellationToken ct)
    {
        var labels = name.TrimEnd('.').Split('.');
        for (var start = 0; start < labels.Length - 1; start++)
        {
            var candidate = string.Join('.', labels[start..]);
            var zones = await SendAsync(HttpMethod.Get, $"zones?name={Uri.EscapeDataString(candidate)}", null, ct) as JsonArray;
            if (zones is [JsonObject zone, ..] && (string?)zone["id"] is { } id) return id;
        }
        throw new AcmeException($"Cloudflare Token 看不到 {name} 所在的区域，请检查 Token 的区域权限。");
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(Api, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        using var response = await http.SendAsync(request, ct);
        JsonObject? json = null;
        try { json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject; }
        catch (JsonException) { }
        if (json is not null && json["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && ok)
            return json["result"];
        var message = (json?["errors"] as JsonArray)?.OfType<JsonObject>().Select(item => (string?)item["message"])
            .FirstOrDefault(item => !string.IsNullOrEmpty(item));
        throw new AcmeException(response.StatusCode == System.Net.HttpStatusCode.Forbidden
            ? "Cloudflare Token 没有编辑 DNS 的权限。"
            : $"Cloudflare API 出错：{message ?? $"HTTP {(int)response.StatusCode}"}");
    }
}
