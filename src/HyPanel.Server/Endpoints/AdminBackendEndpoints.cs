namespace HyPanel.Server.Endpoints;

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

    private static async Task<IResult> GenerateAsync(string backendType, HttpRequest request,
        AdminAuthorization authorization, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!BackendDefinitionCatalog.TryGet(backendType, out _)) return Results.NotFound();
        var values = BackendDefinitionCatalog.GenerateDefaults(backendType);
        return Results.Json(new BackendDefaultsResponse(backendType, values),
            ServerJsonSerializerContext.Default.BackendDefaultsResponse);
    }
}
