namespace HyPanel.Server.Releases;

using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

internal sealed class ReleaseCatalog
{
    private static readonly string[] SupportedRids =
    [
        "win-x64", "win-arm64", "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"
    ];

    private readonly HashSet<string> _assetFileNames = new(StringComparer.Ordinal);

    public ReleaseCatalog(IConfiguration configuration)
    {
        var configuredDirectory = configuration["HyPanel:ReleasesDirectory"];
        ReleasesDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configuredDirectory)
                ? Path.Combine(Environment.CurrentDirectory, "releases")
                : configuredDirectory,
            Environment.CurrentDirectory);

        var manifestPath = Path.Combine(ReleasesDirectory, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return;
        }

        using var stream = File.OpenRead(manifestPath);
        Manifest = JsonSerializer.Deserialize(stream, HyPanelJsonSerializerContext.Default.AgentReleaseManifest)
            ?? throw new InvalidOperationException("Release manifest must not be null.");
        Validate(Manifest);

        foreach (var asset in Manifest.Assets)
        {
            _assetFileNames.Add(asset.FileName);
        }
    }

    public string ReleasesDirectory { get; }

    public AgentReleaseManifest? Manifest { get; }

    public bool TryGetAssetPath(string fileName, out string path)
    {
        path = string.Empty;
        if (!_assetFileNames.Contains(fileName))
        {
            return false;
        }

        path = Path.Combine(ReleasesDirectory, fileName);
        return File.Exists(path);
    }

    public string GetFixedScriptPath(string fileName) => Path.Combine(ReleasesDirectory, fileName);

    private static void Validate(AgentReleaseManifest manifest)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidOperationException("Release manifest schemaVersion must be 1.");
        }

        if (!IsNonEmptyText(manifest.Version) || manifest.PublishedAt.Offset != TimeSpan.Zero)
        {
            throw new InvalidOperationException("Release manifest version and publishedAt must be valid UTC values.");
        }

        if (manifest.Assets is null || manifest.Assets.Count != SupportedRids.Length)
        {
            throw new InvalidOperationException("Release manifest must contain exactly one asset for every supported RID.");
        }

        var rids = new HashSet<string>(StringComparer.Ordinal);
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in manifest.Assets)
        {
            if (asset is null
                || !SupportedRids.Contains(asset.Rid, StringComparer.Ordinal)
                || !rids.Add(asset.Rid)
                || !IsBasename(asset.FileName)
                || !fileNames.Add(asset.FileName)
                || !IsLowercaseSha256(asset.Sha256)
                || asset.Size <= 0)
            {
                throw new InvalidOperationException("Release manifest contains an invalid asset.");
            }
        }

        if (!rids.SetEquals(SupportedRids))
        {
            throw new InvalidOperationException("Release manifest is missing a supported RID.");
        }
    }

    private static bool IsNonEmptyText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBasename(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && fileName == Path.GetFileName(fileName)
        && fileName is not "." and not ".."
        && !fileName.Contains('/')
        && !fileName.Contains('\\')
        && !fileName.Contains('\0');

    private static bool IsLowercaseSha256(string? hash)
    {
        if (hash is null || hash.Length != 64)
        {
            return false;
        }

        foreach (var character in hash)
        {
            if ((character is < '0' or > '9') && (character is < 'a' or > 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
