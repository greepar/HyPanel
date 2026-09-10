namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;

internal static class ReleaseEndpoints
{
    private static readonly TimeSpan EnrollmentTokenLifetime = TimeSpan.FromMinutes(15);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/releases/v1/manifest", GetManifest);
        endpoints.MapGet("/api/releases/v1/assets/{fileName}", GetAsset);
        endpoints.MapGet("/api/backend-releases/v1/assets/{fileName}", GetBackendAssetAsync);
        endpoints.MapGet("/install.sh", GetUnixInstaller);
        endpoints.MapGet("/install.ps1", GetPowerShellInstaller);
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/install-command", CreateInstallCommandAsync);
    }

    private static IResult GetManifest(ReleaseCatalog catalog) =>
        catalog.Manifest is null
            ? Results.NotFound()
            : Results.Json(catalog.Manifest,
                HyPanel.Shared.Serialization.HyPanelJsonSerializerContext.Default.AgentReleaseManifest);

    private static IResult GetAsset(string fileName, ReleaseCatalog catalog)
    {
        return catalog.TryGetAssetPath(fileName, out var path)
            ? Results.File(path, enableRangeProcessing: true)
            : Results.NotFound();
    }

    private static async Task<IResult> GetBackendAssetAsync(
        string fileName,
        HttpRequest request,
        AgentAuthentication authentication,
        BackendArtifactCatalog catalog,
        CancellationToken cancellationToken)
    {
        if (await authentication.AuthenticateAsync(request, cancellationToken) is null)
        {
            return Results.Unauthorized();
        }

        return catalog.TryGetAssetPath(fileName, out var path)
            ? Results.File(path, enableRangeProcessing: true)
            : Results.NotFound();
    }

    private static IResult GetUnixInstaller(ReleaseCatalog catalog) =>
        GetScript(catalog, "install.sh", "text/x-shellscript");

    private static IResult GetPowerShellInstaller(ReleaseCatalog catalog) =>
        GetScript(catalog, "install.ps1", "text/plain");

    private static IResult GetScript(ReleaseCatalog catalog, string fileName, string contentType)
    {
        var path = catalog.GetFixedScriptPath(fileName);
        return File.Exists(path) ? Results.File(path, contentType) : Results.NotFound();
    }

    private static async Task<IResult> CreateInstallCommandAsync(
        Guid nodeId,
        InstallCommandRequest request,
        HttpRequest httpRequest,
        IHostEnvironment environment,
        AdminAuthorization authorization,
        SqliteServerRepository repository,
        EnrollmentService enrollmentService,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);

        if (request.Platform is not ("unix" or "powershell"))
        {
            return Results.BadRequest();
        }

        if (await repository.GetNodeAsync(nodeId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        if (!TryGetBaseUrl(httpRequest, environment, out var baseUrl))
        {
            return Results.BadRequest();
        }

        var token = await enrollmentService.IssueTokenAsync(nodeId, EnrollmentTokenLifetime, cancellationToken);
        var command = request.Platform == "unix"
            ? $"curl -fsSL {ShellQuote(baseUrl + "/install.sh")} | env HYPANEL_PANEL_URL={ShellQuote(baseUrl)} HYPANEL_ENROLLMENT_TOKEN={ShellQuote(token.PlaintextToken)} sh"
            : $"$env:HYPANEL_PANEL_URL = {PowerShellQuote(baseUrl)}; $env:HYPANEL_ENROLLMENT_TOKEN = {PowerShellQuote(token.PlaintextToken)}; irm {PowerShellQuote(baseUrl + "/install.ps1")} | iex";
        return Results.Json(new InstallCommandResponse(command),
            ServerJsonSerializerContext.Default.InstallCommandResponse);
    }

    private static bool TryGetBaseUrl(HttpRequest request, IHostEnvironment environment, out string baseUrl)
    {
        baseUrl = string.Empty;
        if ((environment.IsProduction() &&
             !string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !Uri.CheckSchemeName(request.Scheme)
            || !request.Host.HasValue)
        {
            return false;
        }

        try
        {
            var builder = new UriBuilder(request.Scheme, request.Host.Host, request.Host.Port ?? -1);
            baseUrl = builder.Uri.GetLeftPart(UriPartial.Authority);
            return true;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    private static string ShellQuote(string value) =>
        "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string PowerShellQuote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}