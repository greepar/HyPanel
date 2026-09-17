using System.Text.Json;
using System.IO.Compression;
using System.Formats.Tar;
using HyPanel.Server.Releases;
using HyPanel.Shared.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Releases;

[TestClass]
public sealed class BackendArtifactCatalogTests
{
    [TestMethod]
    public void Constructor_WhenManifestIsValid_LoadsManifestAndFiltersArtifactsByPlatform()
    {
        using var fixture = CatalogFixture.Create();
        fixture.WriteManifest(fixture.Manifest);
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "hysteria2-1.0-linux-x64.tar.gz"), "linux");

        var catalog = fixture.CreateCatalog();

        Assert.IsNotNull(catalog.Manifest);
        Assert.AreEqual(2, catalog.Manifest.Assets.Count);
        var linuxArtifacts = catalog.GetArtifacts("linux-x64");
        Assert.AreEqual(1, linuxArtifacts.Count);
        Assert.AreEqual("1.0", linuxArtifacts[0].Version);
        Assert.AreEqual("linux-x64", linuxArtifacts[0].Rid);
        Assert.AreEqual("hysteria2-1.0-linux-x64.tar.gz", linuxArtifacts[0].FileName);
        Assert.AreEqual(1, catalog.GetArtifacts("win-x64").Count);
        Assert.AreEqual(0, catalog.GetArtifacts("osx-x64").Count);
    }

    [TestMethod]
    public void TryGetAssetPath_WhenListedFileExists_ReturnsPath()
    {
        using var fixture = CatalogFixture.Create();
        fixture.WriteManifest(fixture.Manifest);
        var fileName = fixture.Manifest.Assets[0].FileName;
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, fileName), "asset");

        var catalog = fixture.CreateCatalog();

        Assert.IsTrue(catalog.TryGetAssetPath(fileName, out var path));
        Assert.AreEqual(Path.Combine(fixture.DirectoryPath, fileName), path);
    }

    [TestMethod]
    public void TryGetAssetPath_WhenFileIsUnlistedOrTraversal_ReturnsFalse()
    {
        using var fixture = CatalogFixture.Create();
        fixture.WriteManifest(fixture.Manifest);
        var catalog = fixture.CreateCatalog();

        Assert.IsFalse(catalog.TryGetAssetPath("not-listed.tar.gz", out var unlistedPath));
        Assert.AreEqual(string.Empty, unlistedPath);
        Assert.IsFalse(catalog.TryGetAssetPath("../manifest.json", out var traversalPath));
        Assert.AreEqual(string.Empty, traversalPath);
    }

    [DataTestMethod]
    [DataRow("backend type")]
    [DataRow("unsupported RID")]
    [DataRow("duplicate backend-version-rid")]
    [DataRow("duplicate filename")]
    [DataRow("unsafe filename")]
    [DataRow("uppercase sha")]
    [DataRow("short sha")]
    [DataRow("non-positive size")]
    public void Constructor_WhenAssetIsInvalid_Throws(string invalidCase)
    {
        using var fixture = CatalogFixture.Create();
        var assets = fixture.Manifest.Assets.ToArray();
        assets[0] = invalidCase switch
        {
            "backend type" => assets[0] with { BackendType = "unsupported" },
            "unsupported RID" => assets[0] with { Rid = "unsupported-rid" },
            "duplicate backend-version-rid" => assets[0],
            "duplicate filename" => assets[0],
            "unsafe filename" => assets[0] with { FileName = "../escape.tar.gz" },
            "uppercase sha" => assets[0] with { Sha256 = new string('A', 64) },
            "short sha" => assets[0] with { Sha256 = "abc" },
            "non-positive size" => assets[0] with { Size = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
        };
        if (invalidCase == "duplicate backend-version-rid")
        {
            assets[1] = assets[1] with { Version = assets[0].Version, Rid = assets[0].Rid };
        }
        else if (invalidCase == "duplicate filename")
        {
            assets[1] = assets[1] with { FileName = assets[0].FileName };
        }

        fixture.WriteManifest(fixture.Manifest with { Assets = assets });

        Assert.ThrowsException<InvalidOperationException>(() => fixture.CreateCatalog());
    }

    [TestMethod]
    public void Constructor_WhenSchemaVersionIsNotOne_Throws()
    {
        using var fixture = CatalogFixture.Create();
        fixture.WriteManifest(fixture.Manifest with { SchemaVersion = 2 });

        Assert.ThrowsException<InvalidOperationException>(() => fixture.CreateCatalog());
    }

    [TestMethod]
    public void Constructor_WhenXrayAssetIsValid_AcceptsIt()
    {
        using var fixture = CatalogFixture.Create();
        fixture.WriteManifest(fixture.Manifest with
        {
            Assets = [fixture.Manifest.Assets[0] with { BackendType = "xray" }]
        });

        var artifact = fixture.CreateCatalog().GetArtifacts("linux-x64").Single();

        Assert.AreEqual("xray", artifact.BackendType);
        Assert.AreEqual("1.0", artifact.Version);
    }

    [TestMethod]
    public void Constructor_WhenMihomoAssetIsValid_AcceptsIt()
    {
        using var fixture = CatalogFixture.Create();
        fixture.WriteManifest(fixture.Manifest with
        {
            Assets = [fixture.Manifest.Assets[0] with { BackendType = "mihomo" }]
        });

        var artifact = fixture.CreateCatalog().GetArtifacts("linux-x64").Single();

        Assert.AreEqual("mihomo", artifact.BackendType);
    }

    [TestMethod]
    public void Constructor_WhenSingBoxAssetIsValid_AcceptsIt()
    {
        using var fixture = CatalogFixture.Create();
        fixture.WriteManifest(fixture.Manifest with
        {
            Assets = [fixture.Manifest.Assets[0] with { BackendType = "sing-box" }]
        });

        var artifact = fixture.CreateCatalog().GetArtifacts("linux-x64").Single();

        Assert.AreEqual("sing-box", artifact.BackendType);
    }

    [TestMethod]
    public void ReleaseSources_MapEveryFrozenRidWithoutCallerControlledUrls()
    {
        CollectionAssert.AreEquivalent(new[] { "hysteria2", "xray", "mihomo", "sing-box" },
            BackendReleaseSources.All.Select(item => item.BackendType).ToArray());
        foreach (var source in BackendReleaseSources.All)
        foreach (var rid in ReleaseCatalog.SupportedRids)
        {
            var name = source.AssetName(rid, "1.2.3");
            Assert.IsFalse(string.IsNullOrWhiteSpace(name), $"{source.BackendType} {rid}");
            Assert.AreEqual(Path.GetFileName(name), name);
        }
    }

    [TestMethod]
    public async Task ExtractAsync_SupportsRawGzipZipAndTarGzipExecutables()
    {
        using var fixture = CatalogFixture.Create();
        var payload = System.Text.Encoding.UTF8.GetBytes("backend-binary");
        var raw = Path.Combine(fixture.DirectoryPath, "backend");
        await File.WriteAllBytesAsync(raw, payload);
        var rawOutput = Path.Combine(fixture.DirectoryPath, "raw-output");
        await BackendReleaseSyncWorker.ExtractAsync(raw, "backend", string.Empty, rawOutput, CancellationToken.None);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(rawOutput));

        var gzipPath = Path.Combine(fixture.DirectoryPath, "backend.gz");
        await using (var target = new GZipStream(File.Create(gzipPath), CompressionMode.Compress))
            await target.WriteAsync(payload);
        var gzipOutput = Path.Combine(fixture.DirectoryPath, "gzip-output");
        await BackendReleaseSyncWorker.ExtractAsync(gzipPath, "backend.gz", string.Empty, gzipOutput,
            CancellationToken.None);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(gzipOutput));

        var zipPath = Path.Combine(fixture.DirectoryPath, "backend.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        await using (var target = zip.CreateEntry("backend.exe").Open()) await target.WriteAsync(payload);
        var zipOutput = Path.Combine(fixture.DirectoryPath, "zip-output");
        await BackendReleaseSyncWorker.ExtractAsync(zipPath, "backend.zip", "backend.exe", zipOutput,
            CancellationToken.None);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(zipOutput));

        var tarPath = Path.Combine(fixture.DirectoryPath, "backend.tar.gz");
        await using (var file = File.Create(tarPath))
        await using (var gzip = new GZipStream(file, CompressionMode.Compress))
        await using (var writer = new TarWriter(gzip, leaveOpen: false))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "backend-1.2.3/backend")
                { DataStream = new MemoryStream(payload) };
            await writer.WriteEntryAsync(entry);
        }
        var tarOutput = Path.Combine(fixture.DirectoryPath, "tar-output");
        await BackendReleaseSyncWorker.ExtractAsync(tarPath, "backend.tar.gz", "backend", tarOutput,
            CancellationToken.None);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(tarOutput));
    }

    [TestMethod]
    public void Reload_WhenVersionedIndexIsAdded_ExposesLatestAndCachedArtifact()
    {
        using var fixture = CatalogFixture.Create();
        var catalog = fixture.CreateCatalog();
        var artifact = new BackendArtifact("xray", "2.0.0", "linux-x64", "xray-2.0.0-linux-x64",
            new string('a', 64), 3);
        File.WriteAllBytes(Path.Combine(fixture.DirectoryPath, artifact.FileName), [1, 2, 3]);
        var release = new BackendReleaseIndex(1, "xray", "2.0.0", DateTimeOffset.Parse("2026-09-16T00:00:00Z"),
            [new BackendSourceAsset("linux-x64", "Xray-linux-64.zip",
                "https://github.com/XTLS/Xray-core/releases/download/v2.0.0/Xray-linux-64.zip")], [artifact]);
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, "release-xray-2.0.0.json"),
            JsonSerializer.Serialize(release, BackendArtifactManifestJsonContext.Default.BackendReleaseIndex));

        catalog.Reload();

        Assert.AreEqual("2.0.0", catalog.GetLatestVersion("xray"));
        Assert.AreEqual(artifact, catalog.FindArtifact("xray", "2.0.0", "linux-x64"));
    }

    private static BackendArtifactManifest CreateManifest() => new(
        1,
        "2026.09",
        [
            new BackendArtifact("hysteria2", "1.0", "linux-x64", "hysteria2-1.0-linux-x64.tar.gz", new string('a', 64),
                10),
            new BackendArtifact("hysteria2", "2.0", "win-x64", "hysteria2-2.0-win-x64.zip", new string('b', 64), 20),
        ]);

    private sealed class CatalogFixture : IDisposable
    {
        private CatalogFixture(string directoryPath, BackendArtifactManifest manifest)
        {
            DirectoryPath = directoryPath;
            Manifest = manifest;
        }

        public string DirectoryPath { get; }

        public BackendArtifactManifest Manifest { get; }

        public static CatalogFixture Create() => new(
            Directory.CreateTempSubdirectory("HyPanel.Server.Tests-").FullName,
            CreateManifest());

        public void WriteManifest(BackendArtifactManifest manifest)
        {
            var json = JsonSerializer.Serialize(manifest,
                BackendArtifactManifestJsonContext.Default.BackendArtifactManifest);
            File.WriteAllText(Path.Combine(DirectoryPath, "manifest.json"), json);
        }

        public BackendArtifactCatalog CreateCatalog() => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyPanel:BackendReleasesDirectory"] = DirectoryPath
            })
            .Build());

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
