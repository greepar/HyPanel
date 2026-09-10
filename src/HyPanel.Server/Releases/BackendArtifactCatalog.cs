namespace HyPanel.Server.Releases;

using System.Text.Json;
using System.Text.Json.Serialization;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

internal sealed class BackendArtifactCatalog
{
    private static readonly string[] SupportedRids =
    [
        "win-x64", "win-arm64", "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"
    ];

    private readonly Dictionary<string, BackendArtifact> _assetsByFileName = new(StringComparer.Ordinal);

    public BackendArtifactCatalog(IConfiguration configuration)
    {
        var configuredDirectory = configuration["HyPanel:BackendReleasesDirectory"];
        ReleasesDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "backend-releases")
                : configuredDirectory,
            Environment.CurrentDirectory);

        var manifestPath = Path.Combine(ReleasesDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        using var stream = File.OpenRead(manifestPath);
        Manifest = JsonSerializer.Deserialize(stream,
                       BackendArtifactManifestJsonContext.Default.BackendArtifactManifest)
                   ?? throw new InvalidOperationException("Backend artifact manifest must not be null.");
        Validate(Manifest);
        foreach (var asset in Manifest.Assets)
        {
            _assetsByFileName.Add(asset.FileName, asset);
        }
    }

    public string ReleasesDirectory { get; }

    public BackendArtifactManifest? Manifest { get; }

    public IReadOnlyList<BackendArtifact> GetArtifacts(string platform) =>
        Manifest is null ? [] : Manifest.Assets.Where(asset => asset.Rid == platform).ToArray();

    public bool TryGetAssetPath(string fileName, out string path)
    {
        path = string.Empty;
        if (!_assetsByFileName.ContainsKey(fileName))
        {
            return false;
        }

        path = Path.Combine(ReleasesDirectory, fileName);
        return File.Exists(path);
    }

    private static void Validate(BackendArtifactManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || !IsText(manifest.Version) || manifest.Assets is null)
        {
            throw new InvalidOperationException("Backend artifact manifest is invalid.");
        }

        var combinations = new HashSet<string>(StringComparer.Ordinal);
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            if (asset is null || asset.BackendType is not ("hysteria2" or "xray" or "mihomo" or "sing-box") || !IsText(asset.Version)
                || !SupportedRids.Contains(asset.Rid, StringComparer.Ordinal) || !IsBasename(asset.FileName)
                || !fileNames.Add(asset.FileName) || !IsLowercaseSha256(asset.Sha256) || asset.Size <= 0
                || !combinations.Add($"{asset.BackendType}\n{asset.Version}\n{asset.Rid}"))
            {
                throw new InvalidOperationException("Backend artifact manifest contains an invalid asset.");
            }
        }
    }

    private static bool IsText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character => !char.IsControl(character));

    private static bool IsBasename(string? fileName) => !string.IsNullOrWhiteSpace(fileName) &&
                                                        fileName == Path.GetFileName(fileName)
                                                        && fileName is not "." and not ".." &&
                                                        !fileName.Contains('/') && !fileName.Contains('\\') &&
                                                        !fileName.Contains('\0');

    private static bool IsLowercaseSha256(string? hash) => hash is { Length: 64 } &&
                                                           hash.All(character =>
                                                               (character is >= '0' and <= '9') ||
                                                               (character is >= 'a' and <= 'f'));
}

internal sealed record BackendArtifactManifest(
    int SchemaVersion,
    string Version,
    IReadOnlyList<BackendArtifact> Assets);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackendArtifactManifest))]
internal sealed partial class BackendArtifactManifestJsonContext : JsonSerializerContext;
