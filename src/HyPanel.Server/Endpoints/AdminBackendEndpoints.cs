namespace HyPanel.Server.Endpoints;

using System.Security.Cryptography;
using System.Text.Json;
using HyPanel.Server.Persistence;
using HyPanel.Shared.Serialization;
using HyPanel.Server.Backends;
using HyPanel.Server.Releases;

internal sealed record BackendDefinitionResponse(
    string BackendType,
    string DisplayName,
    string Core,
    string Protocol,
    string Description,
    string Badge,
    string? DefaultVersion,
    BackendFieldDefinition[] Fields);

internal sealed record BackendDefaultsResponse(string BackendType, Dictionary<string, string> Values);

internal static class AdminBackendEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/backends", ListAsync);
        endpoints.MapPost("/api/admin/v1/backends/{backendType}/defaults", GenerateAsync);
    }

    private static async Task<IResult> ListAsync(HttpRequest request, AdminAuthorization authorization,
        BackendArtifactCatalog catalog, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var response = BackendDefinitionCatalog.All.Select(item => new BackendDefinitionResponse(
            item.BackendType,
            item.DisplayName,
            item.Core,
            item.Protocol,
            item.Description,
            item.Badge,
            catalog.GetLatestVersion(item.BackendType) ?? item.FallbackVersion,
            item.Fields)).ToArray();
        return Results.Json(response, ServerJsonSerializerContext.Default.BackendDefinitionResponseArray);
    }

    private static async Task<IResult> GenerateAsync(string backendType, Guid? nodeId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!BackendDefinitionCatalog.TryGet(backendType, out _)) return Results.NotFound();
        var values = BackendDefinitionCatalog.GenerateDefaults(backendType);
        if (nodeId is { } node)
            values["listenPort"] = PickFreePort(await UsedPortsAsync(repository, node, ct))
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Json(new BackendDefaultsResponse(backendType, values),
            ServerJsonSerializerContext.Default.BackendDefaultsResponse);
    }

    // Agent control ports are assigned sequentially from 20000 (see AgentSyncEndpoints).
    private const int ReservedControlPortStart = 20_000;
    private const int ReservedControlPortEnd = 20_255;
    internal const int MinimumSuggestedPort = 10_000;
    internal const int MaximumSuggestedPort = 60_000;

    /// <summary>Ports the node reports as bound, plus listen ports of services already defined on it.</summary>
    private static async Task<HashSet<int>> UsedPortsAsync(SqliteServerRepository repository, Guid nodeId,
        CancellationToken ct)
    {
        var used = new HashSet<int>();
        var observation = (await repository.GetNodeObservationsAsync(ct)).FirstOrDefault(item => item.Id == nodeId);
        if (observation?.LatestMetricSnapshotJson is { } json)
        {
            try
            {
                var metrics = JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.NodeMetrics);
                used.UnionWith(metrics?.ListeningTcpPorts ?? []);
                used.UnionWith(metrics?.ListeningUdpPorts ?? []);
            }
            catch (JsonException)
            {
            }
        }
        foreach (var item in await repository.GetServicesForNodeAsync(nodeId, ct))
        {
            try
            {
                using var document = JsonDocument.Parse(item.Service.ConfigJson);
                if (document.RootElement.TryGetProperty("listenPort", out var port) && port.TryGetInt32(out var value))
                    used.Add(value);
            }
            catch (JsonException)
            {
            }
        }
        return used;
    }

    internal static int PickFreePort(IReadOnlySet<int> used)
    {
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var port = RandomNumberGenerator.GetInt32(MinimumSuggestedPort, MaximumSuggestedPort);
            if (port is >= ReservedControlPortStart and <= ReservedControlPortEnd || used.Contains(port)) continue;
            return port;
        }
        for (var port = MinimumSuggestedPort; port < MaximumSuggestedPort; port++)
            if (port is not (>= ReservedControlPortStart and <= ReservedControlPortEnd) && !used.Contains(port))
                return port;
        return 443;
    }
}
