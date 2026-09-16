namespace HyPanel.Server.Releases;

using System.Security.Cryptography;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

internal sealed class ReleaseSyncWorker(
    ILogger<ReleaseSyncWorker> logger,
    IConfiguration configuration,
    IHttpClientFactory clients,
    ReleaseCatalog catalog) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

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
        var client = clients.CreateClient("release-sync");
        using var manifestResponse = await client.GetAsync(manifestUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        manifestResponse.EnsureSuccessStatusCode();
        await using var manifestStream = await manifestResponse.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync(manifestStream,
            HyPanelJsonSerializerContext.Default.AgentReleaseManifest, cancellationToken)
            ?? throw new InvalidDataException("Remote release manifest is empty.");
        ReleaseCatalog.Validate(manifest);
        if (catalog.Manifest?.Version == manifest.Version && manifest.Assets.All(asset =>
                catalog.TryGetAssetPath(asset.FileName, out _))) return;

        Directory.CreateDirectory(catalog.ReleasesDirectory);
        var temporaryDirectory = Path.Combine(catalog.ReleasesDirectory, ".sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var baseUri = new Uri(manifestUri, ".");
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
            catalog.Reload();
            logger.LogInformation("Cached Agent release {Version}.", manifest.Version);
        }
        finally { Directory.Delete(temporaryDirectory, recursive: true); }
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
