namespace HyPanel.Server.Releases;

using HyPanel.Shared.Versioning;

internal static class AgentUpdatePlanner
{
    public static bool ShouldRequestAutoUpdate(string policy, string currentVersion, string latestVersion,
        string? desiredVersion)
    {
        if (policy != "Auto" || !SemanticVersion.TryParse(currentVersion, out var current)
            || !SemanticVersion.TryParse(latestVersion, out var latest) || latest.PreRelease is not null
            || current.CompareTo(latest) >= 0) return false;
        return desiredVersion is null || SemanticVersion.TryParse(desiredVersion, out var desired)
            && desired.CompareTo(latest) < 0;
    }

    public static bool CanRequestManualUpdate(string? currentVersion, string latestVersion) =>
        SemanticVersion.TryParse(currentVersion, out var current)
        && SemanticVersion.TryParse(latestVersion, out var latest)
        && current.CompareTo(latest) < 0;
}
