namespace HyPanel.Server.Endpoints;

using System.Text;
using System.Text.Json;
using HyPanel.Server.Persistence;

internal static class SubscriptionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/s/{token}", GetAsync);

    /// <summary>
    /// Serves the user's subscription as a Mihomo/Clash YAML profile. The legacy <c>format</c> query parameter is
    /// accepted and ignored so previously distributed links keep working.
    /// </summary>
    internal static async Task GetAsync(string token, HttpResponse response, SqliteServerRepository repository,
        CancellationToken ct)
    {
        SetPrivateHeaders(response);
        var services = await repository.GetSubscriptionServicesAsync(token, ct);
        if (services is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var proxies = services.Select(TryProject).Where(static p => p is not null).Cast<SubscriptionProxy>().ToArray();
        var output = RenderMihomo(proxies, await repository.GetMihomoTemplateAsync(ct) ?? MihomoTemplate.Default);
        if (await repository.GetSubscriptionUserInfoAsync(token, ct) is { } info)
        {
            // Read by Clash Verge / Mihomo Party / Stash to show usage and expiry.
            var userInfo = $"upload={info.UploadBytes}; download={info.DownloadBytes}; total={info.TrafficLimitBytes ?? 0}";
            if (info.ExpiresAtUtc is { } expires) userInfo += $"; expire={expires.ToUnixTimeSeconds()}";
            response.Headers["subscription-userinfo"] = userInfo;
        }
        response.Headers["profile-update-interval"] = "12";
        response.Headers.ContentDisposition = "attachment; filename*=UTF-8''HyPanel.yaml";
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/yaml; charset=utf-8";
        await response.WriteAsync(output, Encoding.UTF8, ct);
    }

    private static SubscriptionProxy? TryProject(SubscriptionServiceRecord service) =>
        string.Equals(service.BackendType, "hysteria2", StringComparison.OrdinalIgnoreCase)
            ? TryProjectHysteria2(service)
            : string.Equals(service.BackendType, "xray", StringComparison.OrdinalIgnoreCase)
                ? TryProjectXray(service)
                : service.BackendType is "mihomo" or "sing-box"
                    ? TryProjectShadowsocks(service)
                    : null;

    private static SubscriptionProxy? TryProjectHysteria2(SubscriptionServiceRecord service)
    {
        if (!string.Equals(service.BackendType, "hysteria2", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var document = JsonDocument.Parse(service.ConfigJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            // Each granted user authenticates with their own userpass identity (see Hysteria2Provider).
            var password = $"{Hysteria2UserName(service.UserId)}:{service.Credential}";
            TryString(document.RootElement, "obfsPassword", out var obfsPassword);
            var endpoint = service.PublicEndpoint;
            return new Hysteria2Proxy(service.Name, endpoint.Host, endpoint.Port, endpoint.TlsServerName, password,
                obfsPassword, service.PinnedCertificateSha256);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SubscriptionProxy? TryProjectXray(SubscriptionServiceRecord service)
    {
        try
        {
            using var document = JsonDocument.Parse(service.ConfigJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryString(root, "flow", out var flow) ||
                !TryString(root, "realityPublicKey", out var publicKey) ||
                !TryString(root, "shortId", out var shortId) ||
                !TryString(root, "serverName", out var serverName) ||
                !TryString(root, "fingerprint", out var fingerprint)) return null;

            var endpoint = service.PublicEndpoint;
            return new XrayProxy(service.Name, endpoint.Host, endpoint.Port, service.Credential, flow, serverName, fingerprint,
                publicKey, shortId);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static SubscriptionProxy? TryProjectShadowsocks(SubscriptionServiceRecord service)
    {
        try
        {
            using var document = JsonDocument.Parse(service.ConfigJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !TryString(root, "method", out var method) ||
                !TryString(root, "password", out var password)) return null;
            var endpoint = service.PublicEndpoint;
            return new ShadowsocksProxy(service.Name, endpoint.Host, endpoint.Port, method, password);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Hysteria2UserName(Guid userId) => $"hypanel-{userId:N}";

    private static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return value.Length > 0;
            }
        }

        return false;
    }

    private static string RenderMihomo(IEnumerable<SubscriptionProxy> proxies, string template)
    {
        var rendered = new List<(string Name, string Yaml)>();
        foreach (var proxy in proxies)
        {
            var yaml = new StringBuilder();
            if (proxy is Hysteria2Proxy hysteria)
            {
                yaml.Append("  - name: ").Append(Yaml(hysteria.Name)).Append("\n    type: hysteria2\n    server: ")
                    .Append(Yaml(hysteria.Host)).Append("\n    port: ").Append(hysteria.Port).Append("\n    password: ")
                    .Append(Yaml(hysteria.Password)).Append(hysteria.PinnedSha256 is null
                        ? "\n    skip-cert-verify: false\n"
                        : "\n    skip-cert-verify: true\n    fingerprint: " + Yaml(hysteria.PinnedSha256) + "\n");
                if (!string.IsNullOrEmpty(hysteria.TlsServerName))
                    yaml.Append("    sni: ").Append(Yaml(hysteria.TlsServerName)).Append('\n');
                if (!string.IsNullOrEmpty(hysteria.ObfsPassword))
                    yaml.Append("    obfs: salamander\n    obfs-password: ").Append(Yaml(hysteria.ObfsPassword))
                        .Append('\n');
            }
            else if (proxy is XrayProxy xray)
            {
                yaml.Append("  - name: ").Append(Yaml(xray.Name)).Append("\n    type: vless\n    server: ")
                    .Append(Yaml(xray.Host)).Append("\n    port: ").Append(xray.Port).Append("\n    uuid: ")
                    .Append(Yaml(xray.ClientId)).Append("\n    flow: ").Append(Yaml(xray.Flow))
                    .Append("\n    tls: true\n    servername: ").Append(Yaml(xray.ServerName))
                    .Append("\n    client-fingerprint: ").Append(Yaml(xray.Fingerprint))
                    .Append("\n    reality-opts:\n      public-key: ").Append(Yaml(xray.PublicKey))
                    .Append("\n      short-id: ").Append(Yaml(xray.ShortId)).Append('\n');
            }
            else if (proxy is ShadowsocksProxy shadowsocks)
            {
                yaml.Append("  - name: ").Append(Yaml(shadowsocks.Name)).Append("\n    type: ss\n    server: ")
                    .Append(Yaml(shadowsocks.Host)).Append("\n    port: ").Append(shadowsocks.Port)
                    .Append("\n    cipher: ").Append(Yaml(shadowsocks.Method)).Append("\n    password: ")
                    .Append(Yaml(shadowsocks.Password)).Append("\n    udp: true\n");
            }
            rendered.Add((proxy.Name, yaml.ToString()));
        }

        return MihomoTemplate.Render(template, rendered, Yaml);
    }

    private static string Yaml(string value) => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static void SetPrivateHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private abstract record SubscriptionProxy(string Name, string Host, int Port);

    private sealed record Hysteria2Proxy(
        string Name,
        string Host,
        int Port,
        string? TlsServerName,
        string Password,
        string? ObfsPassword,
        string? PinnedSha256) : SubscriptionProxy(Name, Host, Port);

    private sealed record XrayProxy(
        string Name,
        string Host,
        int Port,
        string ClientId,
        string Flow,
        string ServerName,
        string Fingerprint,
        string PublicKey,
        string ShortId) : SubscriptionProxy(Name, Host, Port);

    private sealed record ShadowsocksProxy(
        string Name,
        string Host,
        int Port,
        string Method,
        string Password) : SubscriptionProxy(Name, Host, Port);
}
