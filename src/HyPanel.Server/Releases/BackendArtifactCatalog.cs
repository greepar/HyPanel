namespace HyPanel.Server.Releases;

using System.Text.Json;
using System.Text.Json.Serialization;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Versioning;

internal sealed class BackendArtifactCatalog
{
    private Snapshot snapshot = new([], [], []);

    public BackendArtifactCatalog(IConfiguration configuration)
    {
        var configuredDirectory = configuration["HyPanel:BackendReleasesDirectory"];
        ReleasesDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(ServerDataDirectory.Resolve(configuration), "backend-releases") : configuredDirectory,
            Environment.CurrentDirectory);
        Reload();
    }

    public string ReleasesDirectory { get; }
    public BackendArtifactManifest? Manifest { get; private set; }

    public IReadOnlyList<BackendArtifact> GetArtifacts(string rid) =>
        Volatile.Read(ref snapshot).Artifacts.Where(asset => asset.Rid == rid).ToArray();

    public string? GetLatestVersion(string backendType) =>
        Volatile.Read(ref snapshot).LatestVersions.TryGetValue(backendType, out var version) ? version : null;

    public BackendReleaseIndex? GetRelease(string backendType, string version) =>
        Volatile.Read(ref snapshot).Releases.TryGetValue(Key(backendType, version), out var release) ? release : null;

    public BackendArtifact? FindArtifact(string backendType, string version, string rid) =>
        Volatile.Read(ref snapshot).Artifacts.SingleOrDefault(asset => asset.BackendType == backendType
            && asset.Version == version && asset.Rid == rid);

    public bool TryGetAssetPath(string fileName, out string path)
    {
        path = string.Empty;
        if (!Volatile.Read(ref snapshot).FileNames.Contains(fileName)) return false;
        path = Path.Combine(ReleasesDirectory, fileName);
        return File.Exists(path);
    }

    public void Reload()
    {
        if (!Directory.Exists(ReleasesDirectory)) return;
        Manifest = null;
        var releases = new Dictionary<string, BackendReleaseIndex>(StringComparer.Ordinal);
        var artifacts = new List<BackendArtifact>();
        var legacyPath = Path.Combine(ReleasesDirectory, "manifest.json");
        if (File.Exists(legacyPath))
        {
            using var stream = File.OpenRead(legacyPath);
            var legacy = JsonSerializer.Deserialize(stream,
                BackendArtifactManifestJsonContext.Default.BackendArtifactManifest)
                ?? throw new InvalidOperationException("Backend artifact manifest must not be null.");
            ValidateLegacy(legacy);
            Manifest = legacy;
            artifacts.AddRange(legacy.Assets);
        }
        foreach (var path in Directory.EnumerateFiles(ReleasesDirectory, "release-*.json"))
        {
            using var stream = File.OpenRead(path);
            var release = JsonSerializer.Deserialize(stream,
                BackendArtifactManifestJsonContext.Default.BackendReleaseIndex)
                ?? throw new InvalidOperationException("Backend release index must not be null.");
            ValidateRelease(release);
            releases[Key(release.BackendType, release.Version)] = release;
            artifacts.AddRange(release.Artifacts.Where(asset =>
                File.Exists(Path.Combine(ReleasesDirectory, asset.FileName))));
        }
        var latest = releases.Values.GroupBy(item => item.BackendType, StringComparer.Ordinal)
            .ToDictionary(group => group.Key,
                group => group.MaxBy(item => SemanticVersion.Parse(item.Version))!.Version, StringComparer.Ordinal);
        Volatile.Write(ref snapshot, new Snapshot(releases, latest,
            artifacts.GroupBy(item => $"{item.BackendType}\n{item.Version}\n{item.Rid}", StringComparer.Ordinal)
                .Select(group => group.Last()).ToArray()));
    }

    internal static void ValidateRelease(BackendReleaseIndex release)
    {
        if (release.SchemaVersion != 1 || !BackendReleaseSources.IsBackendType(release.BackendType)
            || !SemanticVersion.TryParse(release.Version, out _) || release.PublishedAt.Offset != TimeSpan.Zero
            || release.SourceAssets is null || release.Artifacts is null)
            throw new InvalidOperationException("Backend release index is invalid.");
        var sourceRids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in release.SourceAssets)
            if (!ReleaseCatalog.SupportedRids.Contains(source.Rid, StringComparer.Ordinal) || !sourceRids.Add(source.Rid)
                || !ReleaseCatalog.IsBasename(source.AssetName) || !Uri.TryCreate(source.DownloadUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" && uri.Host != "objects.githubusercontent.com")
                throw new InvalidOperationException("Backend release source asset is invalid.");
        foreach (var artifact in release.Artifacts)
            ValidateArtifact(artifact, release.BackendType, release.Version);
    }

    internal static void ValidateArtifact(BackendArtifact artifact, string backendType, string version)
    {
        if (artifact.BackendType != backendType || artifact.Version != version
            || !ReleaseCatalog.SupportedRids.Contains(artifact.Rid, StringComparer.Ordinal)
            || !ReleaseCatalog.IsBasename(artifact.FileName) || !ReleaseCatalog.IsSha256(artifact.Sha256)
            || artifact.Size <= 0)
            throw new InvalidOperationException("Backend artifact is invalid.");
    }

    private static void ValidateLegacy(BackendArtifactManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.Assets is null) throw new InvalidOperationException("Backend artifact manifest is invalid.");
        var combinations = new HashSet<string>(StringComparer.Ordinal);
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in manifest.Assets)
        {
            if (!BackendReleaseSources.IsBackendType(artifact.BackendType)
                || !combinations.Add($"{artifact.BackendType}\n{artifact.Version}\n{artifact.Rid}")
                || !fileNames.Add(artifact.FileName))
                throw new InvalidOperationException("Backend artifact manifest contains a duplicate or unsupported asset.");
            ValidateArtifact(artifact, artifact.BackendType, artifact.Version);
        }
    }

    private static string Key(string backendType, string version) => backendType + "\n" + version;
    private sealed record Snapshot(Dictionary<string, BackendReleaseIndex> Releases,
        Dictionary<string, string> LatestVersions, IReadOnlyList<BackendArtifact> Artifacts)
    {
        public HashSet<string> FileNames { get; } = Artifacts.Select(item => item.FileName).ToHashSet(StringComparer.Ordinal);
    }
}

internal sealed record BackendReleaseIndex(int SchemaVersion, string BackendType, string Version,
    DateTimeOffset PublishedAt, IReadOnlyList<BackendSourceAsset> SourceAssets,
    IReadOnlyList<BackendArtifact> Artifacts);
internal sealed record BackendSourceAsset(string Rid, string AssetName, string DownloadUrl);
internal sealed record BackendArtifactManifest(int SchemaVersion, string Version, IReadOnlyList<BackendArtifact> Assets);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackendArtifactManifest))]
[JsonSerializable(typeof(BackendReleaseIndex))]
[JsonSerializable(typeof(BackendSourceAsset[]))]
internal sealed partial class BackendArtifactManifestJsonContext : JsonSerializerContext;
