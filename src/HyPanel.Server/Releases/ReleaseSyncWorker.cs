namespace HyPanel.Server.Releases;

using System.Security.Cryptography;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using HyPanel.Server.Persistence;

internal sealed class ReleaseSyncWorker(
    ILogger<ReleaseSyncWorker> logger,
    IConfiguration configuration,
    IHttpClientFactory clients,
    ReleaseCatalog catalog,
    SqliteServerRepository? repository = null) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private const int MaximumInstallerSize = 1024 * 1024;
    private static readonly string[] InstallerFileNames = ["install.sh", "install.ps1"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Release synchronization failed; continuing with cached releases."); }
            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var source = configuration["HyPanel:ReleaseManifestUrl"]
                     ?? "https://github.com/greepar/HyPanel/releases/latest/download/manifest.json";
        if (!Uri.TryCreate(source, UriKind.Absolute, out var manifestUri) || manifestUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("HyPanel:ReleaseManifestUrl must be HTTPS.");
        manifestUri = GitHubMirror.Apply(repository is null ? null
            : (await repository.GetGlobalSettingsAsync(cancellationToken)).GithubMirrorBaseUrl, manifestUri);
        var client = clients.CreateClient("release-sync");
        using var manifestResponse = await client.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync(manifestStream,
            HyPanelJsonSerializerContext.Default.AgentReleaseManifest, cancellationToken)
            ?? throw new InvalidDataException("Remote release manifest is empty.");
        ReleaseCatalog.Validate(manifest);
        var assetsCurrent = catalog.Manifest?.Version == manifest.Version && manifest.Assets.All(asset =>
            catalog.TryGetAssetPath(asset.FileName, out _));

        Directory.CreateDirectory(catalog.ReleasesDirectory);
        var temporaryDirectory = Path.Combine(catalog.ReleasesDirectory, ".sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var baseUri = new Uri(manifestUri, ".");
            var checksums = await DownloadChecksumsAsync(client, new Uri(baseUri, "SHA256SUMS"), cancellationToken);
            foreach (var fileName in InstallerFileNames)
                await DownloadBoundedAsync(client, new Uri(baseUri, fileName),
                    Path.Combine(temporaryDirectory, fileName), checksums[fileName], cancellationToken);
            if (!assetsCurrent)
            {
                foreach (var asset in manifest.Assets)
                {
                    var assetUri = new Uri(baseUri, Uri.EscapeDataString(asset.FileName));
                    await DownloadVerifiedAsync(client, assetUri, Path.Combine(temporaryDirectory, asset.FileName), asset,
                        cancellationToken);
                }
                foreach (var asset in manifest.Assets)
                    File.Move(Path.Combine(temporaryDirectory, asset.FileName),
                        Path.Combine(catalog.ReleasesDirectory, asset.FileName), overwrite: true);
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest,
                    HyPanelJsonSerializerContext.Default.AgentReleaseManifest);
                var versionedManifest = Path.Combine(catalog.ReleasesDirectory, $"manifest-{manifest.Version}.json");
                var temporaryVersionedManifest = versionedManifest + ".tmp";
                await File.WriteAllBytesAsync(temporaryVersionedManifest, manifestBytes, cancellationToken);
                File.Move(temporaryVersionedManifest, versionedManifest, overwrite: true);
                var temporaryManifest = Path.Combine(catalog.ReleasesDirectory, ".manifest.json.tmp");
                await File.WriteAllBytesAsync(temporaryManifest, manifestBytes, cancellationToken);
                File.Move(temporaryManifest, Path.Combine(catalog.ReleasesDirectory, "manifest.json"), overwrite: true);
            }
            foreach (var fileName in InstallerFileNames)
                File.Move(Path.Combine(temporaryDirectory, fileName),
                    Path.Combine(catalog.ReleasesDirectory, fileName), overwrite: true);
            catalog.Reload();
            logger.LogInformation("Cached Agent release metadata and installers for {Version}.", manifest.Version);
        }
        finally { Directory.Delete(temporaryDirectory, recursive: true); }
    }

    private static async Task<Dictionary<string, string>> DownloadChecksumsAsync(HttpClient client, Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is <= 0 or > MaximumInstallerSize)
            throw new InvalidDataException("Release checksums size is invalid.");
        var value = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 2 || fields[0].Length != 64 ||
                fields[0].Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Release checksums are invalid.");
            result[fields[1].TrimStart('*')] = fields[0];
        }
        if (InstallerFileNames.Any(fileName => !result.ContainsKey(fileName)))
            throw new InvalidDataException("Release installer checksum is missing.");
        return result;
    }

    private static async Task DownloadBoundedAsync(HttpClient client, Uri uri, string path, string expectedSha256,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is <= 0 or > MaximumInstallerSize)
            throw new InvalidDataException("Release installer size is invalid.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[16384];
        var total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total = checked(total + read);
            if (total > MaximumInstallerSize) throw new InvalidDataException("Release installer is too large.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total == 0) throw new InvalidDataException("Release installer is empty.");
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Release installer verification failed.");
    }

    private static async Task DownloadVerifiedAsync(HttpClient client, Uri uri, string path, AgentReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
            throw new InvalidDataException("Release asset size mismatch.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > asset.Size) throw new InvalidDataException("Release asset size mismatch.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Sha256,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Release asset verification failed.");
    }
}
