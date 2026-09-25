namespace HyPanel.Server;

using HyPanel.Server.Persistence;

/// <summary>
/// The address users and Agents reach the panel at: the configured panel URL (Settings) when set, otherwise the
/// address of the current request. Install commands, subscription links and Agents all use it, so changing the domain
/// only needs the new address saved while the old one still works.
/// </summary>
internal static class PanelAddress
{
    public static async Task<string> ResolveAsync(HttpRequest request, SqliteServerRepository repository,
        CancellationToken cancellationToken) =>
        (await repository.GetGlobalSettingsAsync(cancellationToken)).PanelUrl ?? FromRequest(request);

    public static string FromRequest(HttpRequest request) => $"{request.Scheme}://{request.Host}";

    /// <summary>Empty clears the setting; otherwise an https origin without path, query or credentials.</summary>
    public static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            return false;
        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }
}
