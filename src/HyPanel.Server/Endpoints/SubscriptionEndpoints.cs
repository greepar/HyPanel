namespace HyPanel.Server.Endpoints;

using System.Text;
using System.Text.Json;
using HyPanel.Server.Persistence;

internal static class SubscriptionEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapGet("/s/{token}", GetAsync);

    internal static async Task GetAsync(string token, string? format, HttpResponse response,
        SqliteServerRepository repository, CancellationToken ct)
    {
        SetPrivateHeaders(response);
        format ??= "raw";
        if (format is not ("raw" or "base64" or "mihomo" or "singbox"))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var services = await repository.GetSubscriptionServicesAsync(token, ct);
        if (services is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var proxies = services.Select(TryProject).Where(static p => p is not null).Cast<SubscriptionProxy>().ToArray();
        var raw = string.Join('\n', proxies.Select(static p => p.Uri));
        var output = format switch
        {
            "raw" => raw,
            "base64" => Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)),
            "mihomo" => RenderMihomo(proxies),
            "singbox" => RenderSingBox(proxies),
            _ => string.Empty
        };
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = format is "mihomo" ? "application/yaml; charset=utf-8" :
            format is "singbox" ? "application/json; charset=utf-8" : "text/plain; charset=utf-8";
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
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryString(document.RootElement, "authPassword", out var password)) return null;
            TryString(document.RootElement, "obfsPassword", out var obfsPassword);
            var endpoint = service.PublicEndpoint;
            var host = endpoint.Host.Contains(':') ? $"[{endpoint.Host}]" : endpoint.Host;
            var query = new List<string>();
            if (!string.IsNullOrEmpty(endpoint.TlsServerName))
                query.Add($"sni={Uri.EscapeDataString(endpoint.TlsServerName)}");
            query.Add("insecure=0");
            if (!string.IsNullOrEmpty(obfsPassword))
            {
                query.Add("obfs=salamander");
                query.Add($"obfs-password={Uri.EscapeDataString(obfsPassword)}");
            }

            var uri =
                $"hysteria2://{Uri.EscapeDataString(password)}@{host}:{endpoint.Port}/?{string.Join('&', query)}#{Uri.EscapeDataString(service.Name)}";
            return new Hysteria2Proxy(service.Name, endpoint.Host, endpoint.Port, endpoint.TlsServerName, password,
                obfsPassword, uri);
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
                !TryString(root, "clientId", out var clientId) ||
                !TryString(root, "flow", out var flow) ||
                !TryString(root, "realityPublicKey", out var publicKey) ||
                !TryString(root, "shortId", out var shortId) ||
                !TryString(root, "serverName", out var serverName) ||
                !TryString(root, "fingerprint", out var fingerprint)) return null;

            var endpoint = service.PublicEndpoint;
            var host = BracketIpv6(endpoint.Host);
            var query = string.Join('&', new[]
            {
                Query("encryption", "none"), Query("flow", flow), Query("security", "reality"),
                Query("sni", serverName), Query("fp", fingerprint), Query("pbk", publicKey),
                Query("sid", shortId), Query("type", "tcp")
            });
            var uri =
                $"vless://{Uri.EscapeDataString(clientId)}@{host}:{endpoint.Port}?{query}#{Uri.EscapeDataString(service.Name)}";
            return new XrayProxy(service.Name, endpoint.Host, endpoint.Port, clientId, flow, serverName, fingerprint,
                publicKey, shortId, uri);
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
            var credential = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{method}:{password}"))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var uri =
                $"ss://{credential}@{BracketIpv6(endpoint.Host)}:{endpoint.Port}#{Uri.EscapeDataString(service.Name)}";
            return new ShadowsocksProxy(service.Name, endpoint.Host, endpoint.Port, method, password, uri);
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static string RenderMihomo(IEnumerable<SubscriptionProxy> proxies)
    {
        var items = proxies.ToArray();
        var yaml = new StringBuilder("proxies:\n");
        foreach (var proxy in items)
        {
            if (proxy is Hysteria2Proxy hysteria)
            {
                yaml.Append("  - name: ").Append(Yaml(hysteria.Name)).Append("\n    type: hysteria2\n    server: ")
                    .Append(Yaml(hysteria.Host)).Append("\n    port: ").Append(hysteria.Port).Append("\n    password: ")
                    .Append(Yaml(hysteria.Password)).Append("\n    skip-cert-verify: false\n");
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
        }

        yaml.Append("proxy-groups:\n  - name: HyPanel\n    type: select\n    proxies:");
        if (items.Length == 0) yaml.Append(" []\n");
        else
            foreach (var proxy in items)
                yaml.Append("\n      - ").Append(Yaml(proxy.Name));
        return yaml.Append('\n').ToString();
    }

    private static string RenderSingBox(IEnumerable<SubscriptionProxy> proxies)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("outbounds");
            foreach (var proxy in proxies)
            {
                writer.WriteStartObject();
                if (proxy is Hysteria2Proxy hysteria)
                {
                    writer.WriteString("type", "hysteria2");
                    writer.WriteString("tag", hysteria.Name);
                    writer.WriteString("server", hysteria.Host);
                    writer.WriteNumber("server_port", hysteria.Port);
                    writer.WriteString("password", hysteria.Password);
                    writer.WriteStartObject("tls");
                    writer.WriteBoolean("enabled", true);
                    writer.WriteBoolean("insecure", false);
                    if (!string.IsNullOrEmpty(hysteria.TlsServerName))
                        writer.WriteString("server_name", hysteria.TlsServerName);
                    writer.WriteEndObject();
                    if (!string.IsNullOrEmpty(hysteria.ObfsPassword))
                    {
                        writer.WriteStartObject("obfs");
                        writer.WriteString("type", "salamander");
                        writer.WriteString("password", hysteria.ObfsPassword);
                        writer.WriteEndObject();
                    }
                }
                else if (proxy is XrayProxy xray)
                {
                    writer.WriteString("type", "vless");
                    writer.WriteString("tag", xray.Name);
                    writer.WriteString("server", xray.Host);
                    writer.WriteNumber("server_port", xray.Port);
                    writer.WriteString("uuid", xray.ClientId);
                    writer.WriteString("flow", xray.Flow);
                    writer.WriteString("network", "tcp");
                    writer.WriteStartObject("tls");
                    writer.WriteBoolean("enabled", true);
                    writer.WriteString("server_name", xray.ServerName);
                    writer.WriteStartObject("utls");
                    writer.WriteBoolean("enabled", true);
                    writer.WriteString("fingerprint", xray.Fingerprint);
                    writer.WriteEndObject();
                    writer.WriteStartObject("reality");
                    writer.WriteBoolean("enabled", true);
                    writer.WriteString("public_key", xray.PublicKey);
                    writer.WriteString("short_id", xray.ShortId);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                else if (proxy is ShadowsocksProxy shadowsocks)
                {
                    writer.WriteString("type", "shadowsocks");
                    writer.WriteString("tag", shadowsocks.Name);
                    writer.WriteString("server", shadowsocks.Host);
                    writer.WriteNumber("server_port", shadowsocks.Port);
                    writer.WriteString("method", shadowsocks.Method);
                    writer.WriteString("password", shadowsocks.Password);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Yaml(string value) => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static string BracketIpv6(string host) => host.Contains(':') ? $"[{host}]" : host;

    private static string Query(string key, string value) =>
        $"{Uri.EscapeDataString(key)}={Uri.EscapeDataString(value)}";

    private static void SetPrivateHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    private abstract record SubscriptionProxy(string Name, string Host, int Port, string Uri);

    private sealed record Hysteria2Proxy(
        string Name,
        string Host,
        int Port,
        string? TlsServerName,
        string Password,
        string? ObfsPassword,
        string Uri) : SubscriptionProxy(Name, Host, Port, Uri);

    private sealed record XrayProxy(
        string Name,
        string Host,
        int Port,
        string ClientId,
        string Flow,
        string ServerName,
        string Fingerprint,
        string PublicKey,
        string ShortId,
        string Uri) : SubscriptionProxy(Name, Host, Port, Uri);

    private sealed record ShadowsocksProxy(
        string Name,
        string Host,
        int Port,
        string Method,
        string Password,
        string Uri) : SubscriptionProxy(Name, Host, Port, Uri);
}