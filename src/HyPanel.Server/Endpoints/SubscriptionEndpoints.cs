using System.Text;
using System.Text.Json;
using HyPanel.Server.Backends;
using HyPanel.Server.Persistence;
using HyPanel.Shared.Networking;

namespace HyPanel.Server.Endpoints;

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

        var proxies = services.SelectMany(Project).ToArray();
        var output = RenderMihomo(proxies, await repository.GetMihomoTemplateAsync(ct) ?? MihomoTemplate.Default);
        var info = await repository.GetSubscriptionUserInfoAsync(token, ct);
        // Clients name the profile after profile-title (Clash Verge, Mihomo Party) or the attachment file name;
        // without them they fall back to the last URL segment, i.e. the token.
        var title = info is null ? "HyPanel" : $"HyPanel · {info.Username}";
        response.Headers["profile-title"] = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(title));
        response.Headers.ContentDisposition =
            $"attachment; filename=\"HyPanel.yaml\"; filename*=UTF-8''{Uri.EscapeDataString(title)}.yaml";
        response.Headers["profile-web-page-url"] = $"{response.HttpContext.Request.Scheme}://{response.HttpContext.Request.Host}/";
        if (info is not null)
        {
            // Read by Clash Verge / Mihomo Party / Stash to show usage and expiry.
            var userInfo = $"upload={info.UploadBytes}; download={info.DownloadBytes}; total={info.TrafficLimitBytes ?? 0}";
            if (info.ExpiresAtUtc is { } expires) userInfo += $"; expire={expires.ToUnixTimeSeconds()}";
            response.Headers["subscription-userinfo"] = userInfo;
        }
        response.Headers["profile-update-interval"] = "12";
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/yaml; charset=utf-8";
        await response.WriteAsync(output, Encoding.UTF8, ct);
    }

    /// <summary>The entries of one service: itself, then one per extra line (route) under the route's own credential.</summary>
    private static IEnumerable<SubscriptionProxy> Project(SubscriptionServiceRecord service)
    {
        if (!BackendDefinitionCatalog.TryGet(service.BackendType, out var definition)) return [];
        try
        {
            using var document = JsonDocument.Parse(service.ConfigJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return [];
            var protocol = definition.ServiceProtocol;
            var entries = new List<SubscriptionProxy>();
            if (protocol.Project(service, document.RootElement, service.Name, service.Credential) is not { } main)
                return [];
            if (service.IncludeDefault) entries.Add(main);
            foreach (var route in service.Routes ?? [])
                if (protocol.Project(service, document.RootElement, route.Name,
                        ServiceRoute.Credential(service.Credential, route.RouteId)) is { } entry)
                    entries.Add(entry);
            return entries;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    internal static string Hysteria2UserName(Guid userId) => Hysteria2Protocol.UserName(userId);

    private static string RenderMihomo(IEnumerable<SubscriptionProxy> proxies, string template)
    {
        var rendered = new List<(string Name, string Yaml)>();
        // Mihomo rejects a profile with two proxies of one name; names can meet when a line shares another's name.
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var original in proxies)
        {
            var proxy = original;
            for (var copy = 2; !taken.Add(proxy.Name); copy++) proxy = original with { Name = $"{original.Name} ({copy})" };
            var yaml = new StringBuilder();
            proxy.AppendMihomo(yaml);
            rendered.Add((proxy.Name, yaml.ToString()));
        }

        return MihomoTemplate.Render(template, rendered, SubscriptionProxy.Quote);
    }

    private static void SetPrivateHeaders(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
