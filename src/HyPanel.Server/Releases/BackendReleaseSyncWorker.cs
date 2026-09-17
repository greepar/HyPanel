namespace HyPanel.Server.Releases;

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Versioning;
using HyPanel.Server.Persistence;

internal sealed class BackendReleaseSyncWorker(ILogger<BackendReleaseSyncWorker> logger, IHttpClientFactory clients,
    BackendArtifactCatalog catalog, SqliteServerRepository repository) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private const long MaximumDownloadBytes = 256L * 1024 * 1024;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "Backend release synchronization failed; using cached metadata."); }
            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(catalog.ReleasesDirectory);
        var client = clients.CreateClient("backend-release-sync");
        foreach (var source in BackendReleaseSources.All)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.github.com/repos/{source.Repository}/releases/latest");
                request.Headers.UserAgent.ParseAdd("HyPanel/1.0");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                var github = await JsonSerializer.DeserializeAsync(body,
                    BackendReleaseJsonContext.Default.GitHubRelease, cancellationToken)
                    ?? throw new InvalidDataException("GitHub release response is empty.");
                var version = NormalizeVersion(github.TagName);
                var assets = new List<BackendSourceAsset>();
                foreach (var rid in ReleaseCatalog.SupportedRids)
                {
                    var expected = source.AssetName(rid, version);
                    if (expected is null) continue;
                    var asset = github.Assets.SingleOrDefault(item => item.Name == expected);
                    if (asset is null) continue;
                    assets.Add(new BackendSourceAsset(rid, expected, asset.BrowserDownloadUrl));
                }
                if (assets.Count == 0) throw new InvalidDataException($"Release {github.TagName} has no supported {source.BackendType} assets.");
                var existing = catalog.GetRelease(source.BackendType, version);
                var index = new BackendReleaseIndex(1, source.BackendType, version, github.PublishedAt, assets,
                    existing?.Artifacts ?? []);
                BackendArtifactCatalog.ValidateRelease(index);
                await WriteIndexAsync(index, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException
                                                  or InvalidDataException or InvalidOperationException)
            { logger.LogWarning(exception, "Could not refresh {BackendType}; retaining cached metadata.", source.BackendType); }
        }
        catalog.Reload();
        await ApplyAutomaticUpdatesAsync(cancellationToken);
    }

    private async Task ApplyAutomaticUpdatesAsync(CancellationToken cancellationToken)
    {
        foreach (var target in await repository.GetAutomaticBackendUpdateTargetsAsync(cancellationToken))
        {
            var latest = catalog.GetLatestVersion(target.BackendType);
            if (latest is null || !SemanticVersion.TryParse(target.DesiredVersion, out var current)
                || !SemanticVersion.TryParse(latest, out var available) || current.CompareTo(available) >= 0) continue;
            try
            {
                await EnsureCachedAsync(target.BackendType, latest, target.ReportedRid, cancellationToken);
                await repository.RequestBackendUpdateAsync(target.NodeId, target.ServiceId, latest, cancellationToken);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException
                                                  or InvalidOperationException)
            {
                logger.LogWarning(exception, "Automatic backend update preparation failed for service {ServiceId}.",
                    target.ServiceId);
            }
        }
    }

    public async Task<BackendArtifact> EnsureCachedAsync(string backendType, string version, string rid,
        CancellationToken cancellationToken)
    {
        if (catalog.FindArtifact(backendType, version, rid) is { } cached) return cached;
        var release = catalog.GetRelease(backendType, version)
            ?? throw new InvalidOperationException("Backend release metadata is unavailable.");
        var sourceAsset = release.SourceAssets.SingleOrDefault(item => item.Rid == rid)
            ?? throw new InvalidOperationException("Backend release is unavailable for this Agent RID.");
        var source = BackendReleaseSources.Get(backendType);
        var client = clients.CreateClient("backend-release-sync");
        var temporary = Path.Combine(catalog.ReleasesDirectory, ".backend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var archive = Path.Combine(temporary, sourceAsset.AssetName);
            await DownloadAsync(client, new Uri(sourceAsset.DownloadUrl), archive, cancellationToken);
            var fileName = $"{backendType}-{version}-{rid}{(rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : string.Empty)}";
            var extracted = Path.Combine(temporary, fileName);
            await ExtractAsync(archive, sourceAsset.AssetName, source.ExecutablePath(rid), extracted, cancellationToken);
            await using var stream = File.OpenRead(extracted);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
            var artifact = new BackendArtifact(backendType, version, rid, fileName, sha, stream.Length);
            BackendArtifactCatalog.ValidateArtifact(artifact, backendType, version);
            File.Move(extracted, Path.Combine(catalog.ReleasesDirectory, fileName), overwrite: true);
            var updated = release with { Artifacts = release.Artifacts.Where(item => item.Rid != rid).Append(artifact).ToArray() };
            await WriteIndexAsync(updated, cancellationToken);
            catalog.Reload();
            logger.LogInformation("Cached backend {BackendType} {Version} for {Rid}.", backendType, version, rid);
            return artifact;
        }
        finally { Directory.Delete(temporary, recursive: true); }
    }

    private async Task WriteIndexAsync(BackendReleaseIndex index, CancellationToken cancellationToken)
    {
        var path = Path.Combine(catalog.ReleasesDirectory, $"release-{index.BackendType}-{index.Version}.json");
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(index,
            BackendArtifactManifestJsonContext.Default.BackendReleaseIndex), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private static string NormalizeVersion(string tag)
    {
        var value = tag.StartsWith("app/v", StringComparison.Ordinal) ? tag[5..]
            : tag.StartsWith('v') ? tag[1..] : tag;
        return SemanticVersion.TryParse(value, out var parsed) && parsed.PreRelease is null ? value
            : throw new InvalidDataException("Latest backend release tag is not stable SemVer.");
    }

    private static async Task DownloadAsync(HttpClient client, Uri uri, string path, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var finalUri = response.RequestMessage?.RequestUri;
        if (finalUri is null || finalUri.Scheme != Uri.UriSchemeHttps || !IsAllowedDownloadHost(finalUri.Host))
            throw new InvalidDataException("Backend release redirected to an untrusted host.");
        if (response.Content.Headers.ContentLength is > MaximumDownloadBytes) throw new InvalidDataException("Backend release is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        var buffer = new byte[65536]; long total = 0;
        while (true) { var read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) break;
            total = checked(total + read); if (total > MaximumDownloadBytes) throw new InvalidDataException("Backend release is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); }
    }

    internal static async Task ExtractAsync(string archive, string assetName, string expectedMember, string output,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(expectedMember))
        {
            if (assetName.EndsWith(".gz", StringComparison.Ordinal))
            { await using var input = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress); await using var target = File.Create(output); await CopyBoundedAsync(input, target, cancellationToken); }
            else File.Copy(archive, output);
            return;
        }
        if (assetName.EndsWith(".zip", StringComparison.Ordinal))
        {
            using var zip = ZipFile.OpenRead(archive);
            var matches = expectedMember == "*.exe"
                ? zip.Entries.Where(item => !item.FullName.Contains('/') && !item.FullName.Contains('\\')
                    && item.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToArray()
                : zip.Entries.Where(item => item.FullName == expectedMember).ToArray();
            if (matches.Length != 1 || matches[0].Length <= 0) throw new InvalidDataException("Backend archive executable is missing.");
            if (matches[0].Length > MaximumDownloadBytes) throw new InvalidDataException("Backend executable is too large.");
            await using var input = matches[0].Open(); await using var target = File.Create(output); await CopyBoundedAsync(input, target, cancellationToken); return;
        }
        await using var file = File.OpenRead(archive); await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip); TarEntry? entry; var found = false;
        while ((entry = await tar.GetNextEntryAsync(cancellationToken: cancellationToken)) is not null)
        {
            if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink) throw new InvalidDataException("Backend archive links are forbidden.");
            if (entry.Name.EndsWith('/' + expectedMember, StringComparison.Ordinal) || entry.Name == expectedMember)
            { if (found || entry.DataStream is null) throw new InvalidDataException("Backend archive executable is ambiguous."); found = true; await using var target = File.Create(output); await CopyBoundedAsync(entry.DataStream, target, cancellationToken); }
        }
        if (!found) throw new InvalidDataException("Backend archive executable is missing.");
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[65536]; long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) return;
            total = checked(total + read); if (total > MaximumDownloadBytes) throw new InvalidDataException("Backend executable is too large.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsAllowedDownloadHost(string host) => host is "github.com" or "objects.githubusercontent.com"
        or "release-assets.githubusercontent.com";
}

internal sealed record GitHubRelease(string TagName, DateTimeOffset PublishedAt, IReadOnlyList<GitHubReleaseAsset> Assets);
internal sealed record GitHubReleaseAsset(string Name, string BrowserDownloadUrl, long Size = 0, string? Digest = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class BackendReleaseJsonContext : JsonSerializerContext;
