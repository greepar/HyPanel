using System.Text.Json;
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