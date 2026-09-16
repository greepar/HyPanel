namespace HyPanel.Server.Releases;

using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using HyPanel.Shared.Versioning;

internal sealed class ReleaseCatalog
{
    internal static readonly string[] SupportedRids =
    [
        "win-x64", "win-arm64", "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"
    ];
    private Snapshot _snapshot = new(null, [], new Dictionary<string, AgentReleaseManifest>(StringComparer.Ordinal));

    public ReleaseCatalog(IConfiguration configuration)
    {
        var configuredDirectory = configuration["HyPanel:ReleasesDirectory"];
        ReleasesDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.CurrentDirectory, "releases") : configuredDirectory, Environment.CurrentDirectory);
        Reload();
    }

    public string ReleasesDirectory { get; }
    public AgentReleaseManifest? Manifest => Volatile.Read(ref _snapshot).Manifest;

    public void Reload()
    {
        var manifestPath = Path.Combine(ReleasesDirectory, "manifest.json");
        if (!File.Exists(manifestPath)) return;
        var manifests = new Dictionary<string, AgentReleaseManifest>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(ReleasesDirectory, "manifest*.json"))
        {
            using var stream = File.OpenRead(path);
            var item = JsonSerializer.Deserialize(stream, HyPanelJsonSerializerContext.Default.AgentReleaseManifest)
                       ?? throw new InvalidOperationException("Release manifest must not be null.");
            Validate(item);
            manifests[item.Version] = item;
        }
        var manifest = manifests.Values.MaxBy(item => HyPanel.Shared.Versioning.SemanticVersion.Parse(item.Version));
        Volatile.Write(ref _snapshot, new Snapshot(manifest,
            manifests.Values.SelectMany(item => item.Assets).Select(asset => asset.FileName).ToHashSet(StringComparer.Ordinal),
            manifests));
    }

    public AgentReleaseAsset? FindAsset(string version, string rid)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Manifests.TryGetValue(version, out var manifest)
            ? manifest.Assets.SingleOrDefault(asset => asset.Rid == rid) : null;
    }

    public bool TryGetAssetPath(string fileName, out string path)
    {
        path = string.Empty;
        if (!Volatile.Read(ref _snapshot).FileNames.Contains(fileName)) return false;
        path = Path.Combine(ReleasesDirectory, fileName);
        return File.Exists(path);
    }

    public string GetFixedScriptPath(string fileName) => Path.Combine(ReleasesDirectory, fileName);

    internal static void Validate(AgentReleaseManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidOperationException("Release manifest schemaVersion must be 1.");
        if (!SemanticVersion.TryParse(manifest.Version, out _) || manifest.PublishedAt.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("Release manifest version and publishedAt must be valid UTC values.");
        if (manifest.Assets is null || manifest.Assets.Count != SupportedRids.Length)
            throw new InvalidOperationException("Release manifest must contain exactly one asset for every supported RID.");
        var rids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            if (asset is null || !SupportedRids.Contains(asset.Rid, StringComparer.Ordinal) || !rids.Add(asset.Rid)
                || !IsBasename(asset.FileName) || !IsExpectedAssetName(manifest.Version, asset)
                || !names.Add(asset.FileName) || !IsSha256(asset.Sha256) || asset.Size <= 0)
                throw new InvalidOperationException("Release manifest contains an invalid asset.");
        }
        if (!rids.SetEquals(SupportedRids)) throw new InvalidOperationException("Release manifest is missing a supported RID.");
    }

    internal static bool IsBasename(string? fileName) => !string.IsNullOrWhiteSpace(fileName)
        && fileName == Path.GetFileName(fileName) && fileName is not "." and not ".."
        && !fileName.Contains('/') && !fileName.Contains('\\') && !fileName.Contains('\0');
    internal static bool IsSha256(string? hash) => hash is { Length: 64 }
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsExpectedAssetName(string version, AgentReleaseAsset asset) =>
        asset.FileName == $"hypanel-agent-{version}-{asset.Rid}.{(asset.Rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}";
    private sealed record Snapshot(AgentReleaseManifest? Manifest, HashSet<string> FileNames,
        Dictionary<string, AgentReleaseManifest> Manifests);
}
