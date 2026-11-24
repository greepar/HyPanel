using System.Text.Json;
using HyPanel.Shared.Networking;

namespace HyPanel.Server.Backends;

/// <summary>
/// The lines of a service and which of them leave through an upstream proxy: the default line (the service's own
/// upstream fields) and each extra route. Direct-only groups may take only the direct ones.
/// </summary>
internal sealed record ServiceLines(bool DefaultProxied, IReadOnlyList<ServiceRoute> Routes)
{
    /// <summary>The first Agent that leaves proxied lines out for direct-only users; older ones would admit them.</summary>
    public const string DirectOnlyMinimumAgentVersion = "0.5.39";

    public bool AnyProxied => DefaultProxied || Routes.Any(static route => route.Proxy is not null);

    public bool AnyDirect => !DefaultProxied || Routes.Any(static route => route.Proxy is null);

    public static ServiceLines Of(string backendType, string configJson)
    {
        var routes = BackendDefinitionCatalog.Has(backendType, BackendFeatures.Routes) ? ServiceRoute.Parse(configJson) : [];
        try
        {
            using var document = JsonDocument.Parse(configJson);
            var root = document.RootElement;
            var proxied = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("upstreamProxyType", out var type)
                          && type.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(type.GetString());
            return new ServiceLines(proxied, routes);
        }
        catch (JsonException)
        {
            return new ServiceLines(false, routes);
        }
    }
}
