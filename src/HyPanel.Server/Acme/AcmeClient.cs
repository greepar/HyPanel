using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HyPanel.Server.Acme;

/// <summary>An ACME server answered with a problem document (RFC 8555 section 6.7).</summary>
internal sealed class AcmeException(string message, string? type = null) : Exception(message)
{
    public string? ProblemType { get; } = type;
}

/// <summary>An authorization of an order: the domain to prove and the challenges offered for it.</summary>
internal sealed record AcmeAuthorization(string Url, string Status, string Domain, bool Wildcard,
    IReadOnlyList<AcmeChallenge> Challenges);

internal sealed record AcmeChallenge(string Type, string Url, string Token, string Status);

internal sealed record AcmeOrder(string Url, string Status, IReadOnlyList<string> Authorizations, string Finalize,
    string? Certificate);

/// <summary>
/// A minimal ACME v2 client (RFC 8555) for one account: ES256 requests signed by the account key, new orders for one
/// name, challenge responses and finalization. Built on JsonNode so it needs no reflection under NativeAOT.
/// </summary>
internal sealed class AcmeClient(HttpClient http, Uri directoryUrl, ECDsa accountKey, string? accountUrl)
{
    private const int MaximumResponseBytes = 1024 * 1024;
    // RFC 8555 section 6.1: clients must send a User-Agent; Pebble rejects requests without one.
    private const string UserAgent = "HyPanel-ACME/1.0";
    private JsonObject? directory;
    private string? nonce;

    /// <summary>The account's URL (its <c>kid</c>), known after <see cref="EnsureAccountAsync"/>.</summary>
    public string? AccountUrl { get; private set; } = accountUrl;

    /// <summary>RFC 7638 thumbprint of the account key; the second half of every key authorization.</summary>
    public string Thumbprint => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(JwkJson(accountKey))));

    public string KeyAuthorization(string token) => $"{token}.{Thumbprint}";

    /// <summary>The TXT value a <c>dns-01</c> challenge expects at <c>_acme-challenge.&lt;domain&gt;</c>.</summary>
    public string DnsValue(string token) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(KeyAuthorization(token))));

    /// <summary>Registers the account (or finds the existing one for this key) and returns its URL.</summary>
    public async Task<string> EnsureAccountAsync(string email, CancellationToken ct)
    {
        if (AccountUrl is not null) return AccountUrl;
        var payload = new JsonObject
        {
            ["termsOfServiceAgreed"] = true,
            ["contact"] = new JsonArray((JsonNode)$"mailto:{email}")
        };
        using var response = await PostAsync(await EndpointAsync("newAccount", ct), payload, ct, useJwk: true);
        AccountUrl = response.Headers.Location?.ToString() ?? throw new AcmeException("ACME 服务器没有返回账户地址。");
        return AccountUrl;
    }

    public async Task<AcmeOrder> NewOrderAsync(string domain, CancellationToken ct)
    {
        var payload = new JsonObject
        {
            ["identifiers"] = new JsonArray(new JsonObject { ["type"] = "dns", ["value"] = domain })
        };
        using var response = await PostAsync(await EndpointAsync("newOrder", ct), payload, ct);
        var url = response.Headers.Location?.ToString() ?? throw new AcmeException("ACME 服务器没有返回订单地址。");
        return ReadOrder(url, await ReadJsonAsync(response, ct));
    }

    public async Task<AcmeOrder> GetOrderAsync(string url, CancellationToken ct)
    {
        using var response = await PostAsync(url, null, ct);
        return ReadOrder(url, await ReadJsonAsync(response, ct));
    }

    public async Task<AcmeAuthorization> GetAuthorizationAsync(string url, CancellationToken ct)
    {
        using var response = await PostAsync(url, null, ct);
        var json = await ReadJsonAsync(response, ct);
        var challenges = (json["challenges"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select(item => new AcmeChallenge((string?)item["type"] ?? "", (string?)item["url"] ?? "",
                (string?)item["token"] ?? "", (string?)item["status"] ?? ""))
            .Where(item => item.Url.Length > 0)
            .ToArray();
        return new AcmeAuthorization(url, (string?)json["status"] ?? "", (string?)json["identifier"]?["value"] ?? "",
            json["wildcard"] is JsonValue wildcard && wildcard.TryGetValue<bool>(out var value) && value, challenges);
    }

    /// <summary>Tells the server the challenge is ready to be validated.</summary>
    public async Task RespondAsync(AcmeChallenge challenge, CancellationToken ct)
    {
        using var _ = await PostAsync(challenge.Url, new JsonObject(), ct);
    }

    /// <summary>
    /// Waits until the authorization is valid; throws with the validation error when it becomes invalid or the
    /// server keeps it pending past <paramref name="timeout"/>.
    /// </summary>
    public async Task WaitForAuthorizationAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            using var response = await PostAsync(url, null, ct);
            var json = await ReadJsonAsync(response, ct);
            var status = (string?)json["status"];
            if (status == "valid") return;
            if (status is "invalid" or "deactivated" or "expired" or "revoked")
            {
                var problem = (json["challenges"] as JsonArray ?? []).OfType<JsonObject>()
                    .Select(item => item["error"] as JsonObject).FirstOrDefault(item => item is not null);
                throw new AcmeException(problem is null ? $"验证失败（{status}）。" : Problem(problem),
                    (string?)problem?["type"]);
            }
            if (DateTimeOffset.UtcNow > deadline) throw new AcmeException("证书颁发机构迟迟没有完成验证。");
            await Task.Delay(RetryAfter(response, TimeSpan.FromSeconds(2)), ct);
        }
    }

    /// <summary>Submits the CSR, waits for the certificate and downloads it as a PEM chain.</summary>
    public async Task<string> FinalizeAsync(AcmeOrder order, byte[] csr, TimeSpan timeout, CancellationToken ct)
    {
        var payload = new JsonObject { ["csr"] = Base64Url(csr) };
        using (var _ = await PostAsync(order.Finalize, payload, ct)) { }
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            var current = await GetOrderAsync(order.Url, ct);
            if (current.Status == "valid" && current.Certificate is { } certificate)
            {
                using var response = await PostAsync(certificate, null, ct, accept: "application/pem-certificate-chain");
                var pem = await ReadStringAsync(response, ct);
                if (!pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
                    throw new AcmeException("ACME 服务器返回的证书无效。");
                return pem;
            }
            if (current.Status == "invalid") throw new AcmeException("证书颁发机构拒绝签发。");
            if (DateTimeOffset.UtcNow > deadline) throw new AcmeException("证书颁发机构迟迟没有签发证书。");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>A CSR for <paramref name="domain"/> signed by the certificate's own key.</summary>
    public static byte[] CertificateSigningRequest(string domain, ECDsa certificateKey)
    {
        var request = new CertificateRequest(new X500DistinguishedName($"CN={domain}"), certificateKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(domain);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSigningRequest();
    }

    /// <summary>The public key as a JWK with members in the order RFC 7638 requires for the thumbprint.</summary>
    internal static string JwkJson(ECDsa key)
    {
        var parameters = key.ExportParameters(false);
        return $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url(parameters.Q.X!)}\",\"y\":\"{Base64Url(parameters.Q.Y!)}\"}}";
    }

    internal static string Base64Url(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A JWS in flattened JSON form; a null payload is a POST-as-GET.</summary>
    internal string Sign(string url, string requestNonce, JsonObject? payload, bool useJwk)
    {
        var header = $"{{\"alg\":\"ES256\",{(useJwk ? $"\"jwk\":{JwkJson(accountKey)}" : $"\"kid\":{JsonValue.Create(AccountUrl)!.ToJsonString()}")},\"nonce\":{JsonValue.Create(requestNonce)!.ToJsonString()},\"url\":{JsonValue.Create(url)!.ToJsonString()}}}";
        var protectedPart = Base64Url(Encoding.UTF8.GetBytes(header));
        var payloadPart = payload is null ? "" : Base64Url(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        var signature = accountKey.SignData(Encoding.ASCII.GetBytes($"{protectedPart}.{payloadPart}"), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return new JsonObject
        {
            ["protected"] = protectedPart, ["payload"] = payloadPart, ["signature"] = Base64Url(signature)
        }.ToJsonString();
    }

    private async Task<HttpResponseMessage> PostAsync(string url, JsonObject? payload, CancellationToken ct,
        bool useJwk = false, string accept = "application/json")
    {
        if (!useJwk && AccountUrl is null) throw new InvalidOperationException("The ACME account is not registered yet.");
        for (var attempt = 0; ; attempt++)
        {
            var body = Sign(url, nonce ?? await NewNonceAsync(ct), payload, useJwk);
            nonce = null;
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(body, Encoding.UTF8)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/jose+json");
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.Headers.TryGetValues("Replay-Nonce", out var values)) nonce = values.FirstOrDefault();
            if (response.IsSuccessStatusCode) return response;
            using (response)
            {
                var problem = await TryReadJsonAsync(response, ct);
                var type = (string?)problem?["type"];
                // A stale nonce is expected now and then; the response carried a fresh one.
                if (type == "urn:ietf:params:acme:error:badNonce" && attempt < 3) continue;
                throw new AcmeException(problem is null ? $"ACME 请求失败（HTTP {(int)response.StatusCode}）。" : Problem(problem), type);
            }
        }
    }

    private async Task<string> NewNonceAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, await EndpointAsync("newNonce", ct));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, ct);
        return response.Headers.TryGetValues("Replay-Nonce", out var values) && values.FirstOrDefault() is { } value
            ? value
            : throw new AcmeException("ACME 服务器没有返回 nonce。");
    }

    private async Task<string> EndpointAsync(string name, CancellationToken ct)
    {
        if (directory is null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, directoryUrl);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new AcmeException($"无法读取 ACME 目录（HTTP {(int)response.StatusCode}）。");
            directory = await ReadJsonAsync(response, ct);
        }
        return (string?)directory[name] ?? throw new AcmeException($"ACME 目录缺少 {name}。");
    }

    private static AcmeOrder ReadOrder(string url, JsonObject json) => new(url, (string?)json["status"] ?? "",
        [.. (json["authorizations"] as JsonArray ?? []).Select(item => (string?)item ?? "").Where(item => item.Length > 0)],
        (string?)json["finalize"] ?? throw new AcmeException("ACME 订单缺少 finalize 地址。"),
        (string?)json["certificate"]);

    private static string Problem(JsonObject problem)
    {
        var detail = (string?)problem["detail"];
        var type = ((string?)problem["type"])?.Replace("urn:ietf:params:acme:error:", "", StringComparison.Ordinal);
        return string.IsNullOrEmpty(detail) ? type ?? "未知错误" : type is null ? detail : $"{detail}（{type}）";
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response, TimeSpan fallback) =>
        response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(30)
            ? delta
            : fallback;

    private static async Task<JsonObject> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct) =>
        await TryReadJsonAsync(response, ct) ?? throw new AcmeException("ACME 服务器返回了无法识别的内容。");

    private static async Task<JsonObject?> TryReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return JsonNode.Parse(await ReadStringAsync(response, ct)) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static async Task<string> ReadStringAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new AcmeException("ACME 服务器的响应过大。");
        var text = await response.Content.ReadAsStringAsync(ct);
        return text.Length > MaximumResponseBytes ? throw new AcmeException("ACME 服务器的响应过大。") : text;
    }
}
