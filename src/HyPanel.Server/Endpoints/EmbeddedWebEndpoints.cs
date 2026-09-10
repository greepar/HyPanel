namespace HyPanel.Server.Endpoints;

using System.Reflection;

internal static class EmbeddedWebEndpoints
{
    private const string IndexResourceName = "HyPanel.Web/index.html";
    private const string AssetResourcePrefix = "HyPanel.Web/assets/";
    private static readonly Assembly Assembly = typeof(EmbeddedWebEndpoints).Assembly;
    private static readonly HashSet<string> AssetResourceNames = Assembly.GetManifestResourceNames()
        .Where(static name => name.StartsWith(AssetResourcePrefix, StringComparison.Ordinal))
        .ToHashSet(StringComparer.Ordinal);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/", static (HttpContext context) =>
            WriteResourceAsync(context, IndexResourceName, "text/html; charset=utf-8"));
        endpoints.MapGet("/assets/{file}", static (string file, HttpContext context) =>
            WriteAssetAsync(context, file));
    }

    private static Task WriteAssetAsync(HttpContext context, string file)
    {
        if (!IsSafeAssetFileName(file))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        var resourceName = AssetResourcePrefix + file;
        if (!AssetResourceNames.Contains(resourceName))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        return WriteResourceAsync(context, resourceName, GetContentType(file));
    }

    private static async Task WriteResourceAsync(HttpContext context, string resourceName, string contentType)
    {
        await using var resource = Assembly.GetManifestResourceStream(resourceName);
        if (resource is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = contentType;
        context.Response.ContentLength = resource.Length;
        await resource.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    internal static bool IsSafeAssetFileName(string file) =>
        file.Length is > 0 and <= 255 &&
        file.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_') &&
        !file.StartsWith(".", StringComparison.Ordinal) &&
        !file.Contains("..", StringComparison.Ordinal);

    internal static string GetContentType(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };
}
