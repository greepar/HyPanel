using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using HyPanel.Server.Releases;
using HyPanel.Server.Updates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Releases;

[TestClass]
public sealed class ServerUpdateServiceTests
{
    [TestMethod]
    public async Task RefreshAsync_InDockerMode_ReportsLatestButNeverAllowsInPlaceUpdate()
    {
        var release = new GitHubRelease("v99.0.0", DateTimeOffset.Parse("2026-09-17T00:00:00Z"), []);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(release, BackendReleaseJsonContext.Default.GitHubRelease);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HyPanel:DeploymentMode"] = "Docker",
            ["HYPANEL_DATA_DIR"] = Directory.CreateTempSubdirectory("HyPanel.ServerUpdate-").FullName
        }).Build();
        using var service = new ServerUpdateService(configuration,
            new ClientFactory(new StaticHandler(bytes)), new TestLifetime(),
            NullLogger<ServerUpdateService>.Instance);

        await service.RefreshAsync(CancellationToken.None);
        var status = service.GetStatus();

        Assert.AreEqual("Docker", status.DeploymentMode);
        Assert.AreEqual("99.0.0", status.LatestVersion);
        Assert.IsFalse(status.UpdateAvailable);
        Assert.IsFalse(service.CanUpdate(out _));
        Directory.Delete(configuration["HYPANEL_DATA_DIR"]!, recursive: true);
    }

    [TestMethod]
    public async Task ExtractAsync_ExtractsOnlyServerExecutable()
    {
        using var directory = new TemporaryDirectory();
        var archive = Path.Combine(directory.Path, "server.tar.gz");
        var output = Path.Combine(directory.Path, "staged");
        var payload = "native-server"u8.ToArray();
        await using (var file = File.Create(archive))
        await using (var gzip = new GZipStream(file, CompressionMode.Compress))
        using (var writer = new TarWriter(gzip, leaveOpen: false))
        {
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "./HyPanel.Server")
                { DataStream = new MemoryStream(payload) });
            await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, "./appsettings.json")
                { DataStream = new MemoryStream("{}"u8.ToArray()) });
        }

        await ServerUpdateService.ExtractAsync(archive, output, CancellationToken.None);

        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(output));
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
    private sealed class StaticHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent(body) });
    }
    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("HyPanel.ServerUpdate-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
