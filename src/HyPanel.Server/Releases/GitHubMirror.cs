namespace HyPanel.Server.Releases;

internal static class GitHubMirror
{
    public static Uri Apply(string? mirrorBaseUrl, Uri officialUri)
    {
        if (string.IsNullOrWhiteSpace(mirrorBaseUrl)) return officialUri;
        var mirror = new Uri(mirrorBaseUrl.TrimEnd('/') + '/');
        return new Uri(mirror, Uri.EscapeDataString(officialUri.AbsoluteUri));
    }
}
