namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;
using HyPanel.Server.Updates;

internal sealed record UpdateGlobalSettingsRequest(string AgentUpdateDefaultPolicy,
    string BackendUpdateDefaultPolicy, string? GithubMirrorBaseUrl, string? PanelUrl = null);
internal sealed record GlobalSettingsResponse(string AgentUpdateDefaultPolicy, string BackendUpdateDefaultPolicy,
    string? GithubMirrorBaseUrl, string? AgentReleaseVersion, IReadOnlyDictionary<string, string> BackendReleases,
    ServerUpdateStatus Server, string DataDirectory, long DatabaseSizeBytes, string? PanelUrl, string CurrentUrl);

internal static class GlobalSettingsEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/settings", GetAsync);
        endpoints.MapPut("/api/admin/v1/settings", UpdateAsync);
    }

    private static async Task<IResult> GetAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, SqliteConnectionFactory connections, ReleaseCatalog agents, BackendArtifactCatalog backends,
        ServerUpdateService server, IConfiguration configuration, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var settings = await repository.GetGlobalSettingsAsync(ct);
        var versions = BackendReleaseSources.All.ToDictionary(item => item.BackendType,
            item => backends.GetLatestVersion(item.BackendType) ?? "Unavailable", StringComparer.Ordinal);
        var data = ServerDataDirectory.Resolve(configuration);
        var database = connections.DatabasePath;
        var response = new GlobalSettingsResponse(settings.AgentUpdateDefaultPolicy,
            settings.BackendUpdateDefaultPolicy, settings.GithubMirrorBaseUrl, agents.Manifest?.Version, versions,
            server.GetStatus(), data, File.Exists(database) ? new FileInfo(database).Length : 0, settings.PanelUrl,
            PanelAddress.FromRequest(request));
        return Results.Json(response, ServerJsonSerializerContext.Default.GlobalSettingsResponse);
    }

    private static async Task<IResult> UpdateAsync(UpdateGlobalSettingsRequest body, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (body.AgentUpdateDefaultPolicy is not ("Manual" or "Auto")
            || body.BackendUpdateDefaultPolicy is not ("Manual" or "Auto")
            || !TryMirror(body.GithubMirrorBaseUrl, out var mirror)) return Results.BadRequest();
        if (!PanelAddress.TryNormalize(body.PanelUrl, out var panelUrl))
            return Results.Json(new CertificateErrorResponse("面板地址必须是 https:// 开头的域名地址，不能带路径。"),
                ServerJsonSerializerContext.Default.CertificateErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        await repository.SetPanelUrlAsync(panelUrl, ct);
        var updated = await repository.UpdateGlobalSettingsAsync(body.AgentUpdateDefaultPolicy,
            body.BackendUpdateDefaultPolicy, mirror, ct);
        return Results.Json(updated, ServerJsonSerializerContext.Default.GlobalSettingsRecord);
    }

    private static bool TryMirror(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        normalized = uri.AbsoluteUri.TrimEnd('/');
        return true;
    }
}
