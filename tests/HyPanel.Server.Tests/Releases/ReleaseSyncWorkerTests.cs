using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HyPanel.Server.Releases;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Releases;

[TestClass]
public sealed class ReleaseSyncWorkerTests
{
    [TestMethod]
    public async Task RefreshAsync_DownloadsAndAtomicallyPublishesVerifiedRelease()
    {
        using var directory = new TemporaryDirectory();
        var assets = ReleaseCatalog.SupportedRids.Select(rid =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(rid);
            return (Asset: new AgentReleaseAsset(rid,
                $"hypanel-agent-1.3.0-{rid}.{(rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}",
                Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length), Bytes: bytes);
        }).ToArray();
        var manifest = new AgentReleaseManifest(1, "1.3.0", DateTimeOffset.Parse("2026-09-16T00:00:00Z"),
            assets.Select(item => item.Asset).ToArray());
        var handler = new ReleaseHandler(JsonSerializer.SerializeToUtf8Bytes(manifest,
            HyPanelJsonSerializerContext.Default.AgentReleaseManifest), assets.ToDictionary(item => item.Asset.FileName,
            item => item.Bytes));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HyPanel:ReleasesDirectory"] = directory.Path,
            ["HyPanel:ReleaseManifestUrl"] = "https://releases.example/manifest.json",
        }).Build();
        var catalog = new ReleaseCatalog(configuration);
        var worker = new ReleaseSyncWorker(NullLogger<ReleaseSyncWorker>.Instance, configuration,
            new ClientFactory(handler), catalog);

        await worker.RefreshAsync(CancellationToken.None);

        Assert.AreEqual("1.3.0", catalog.Manifest!.Version);
        foreach (var asset in manifest.Assets)
        {
            Assert.IsTrue(catalog.TryGetAssetPath(asset.FileName, out var path));
            CollectionAssert.AreEqual(assets.Single(item => item.Asset.Rid == asset.Rid).Bytes,
                await File.ReadAllBytesAsync(path));
        }
        Assert.IsTrue(File.Exists(System.IO.Path.Combine(directory.Path, "manifest-1.3.0.json")));
    }

    [TestMethod]
    public async Task RefreshAsync_WhenRemoteFails_RetainsCachedRelease()
    {
        using var directory = new TemporaryDirectory();
        var manifest = CreateCachedManifest();
        await File.WriteAllBytesAsync(System.IO.Path.Combine(directory.Path, "manifest.json"),
            JsonSerializer.SerializeToUtf8Bytes(manifest, HyPanelJsonSerializerContext.Default.AgentReleaseManifest));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HyPanel:ReleasesDirectory"] = directory.Path,
            ["HyPanel:ReleaseManifestUrl"] = "https://releases.example/manifest.json",
        }).Build();
        var catalog = new ReleaseCatalog(configuration);
        var worker = new ReleaseSyncWorker(NullLogger<ReleaseSyncWorker>.Instance, configuration,
            new ClientFactory(new FailingHandler()), catalog);

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() => worker.RefreshAsync(CancellationToken.None));

        Assert.AreEqual("1.2.0", catalog.Manifest!.Version);
    }

    private static AgentReleaseManifest CreateCachedManifest() => new(1, "1.2.0",
        DateTimeOffset.Parse("2026-09-15T00:00:00Z"), ReleaseCatalog.SupportedRids.Select(rid => new AgentReleaseAsset(rid,
            $"hypanel-agent-1.2.0-{rid}.{(rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}",
            new string('a', 64), 1)).ToArray());

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ReleaseHandler(byte[] manifest, Dictionary<string, byte[]> assets) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.RequestUri!.AbsolutePath.EndsWith("manifest.json", StringComparison.Ordinal)
                ? manifest : assets[Uri.UnescapeDataString(request.RequestUri.Segments[^1])];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => throw new HttpRequestException("offline");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("HyPanel.ReleaseSync-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
