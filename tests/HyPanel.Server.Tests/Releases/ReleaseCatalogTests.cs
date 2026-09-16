using System.Text.Json;
using HyPanel.Server.Releases;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Releases;

[TestClass]
public sealed class ReleaseCatalogTests
{
    private static readonly string[] SupportedRids =
    [
        "win-x64", "win-arm64", "osx-x64", "osx-arm64",
        "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"
    ];

    [TestMethod]
    public void Constructor_WhenManifestIsValid_LoadsManifestAndResolvesExistingAsset()
    {
        using var fixture = ReleaseFixture.Create();
        var asset = fixture.Manifest.Assets[0];
        File.WriteAllText(Path.Combine(fixture.DirectoryPath, asset.FileName), "asset");
        fixture.WriteManifest(fixture.Manifest);

        var catalog = fixture.CreateCatalog();

        Assert.IsNotNull(catalog.Manifest);
        Assert.AreEqual(1, catalog.Manifest.SchemaVersion);
        Assert.AreEqual(fixture.Manifest.Assets.Count, catalog.Manifest.Assets.Count);
        Assert.IsTrue(catalog.TryGetAssetPath(asset.FileName, out var path));
        Assert.AreEqual(Path.Combine(fixture.DirectoryPath, asset.FileName), path);
    }

    [TestMethod]
    public void Constructor_WhenManifestDoesNotExist_SetsManifestToNull()
    {
        using var fixture = ReleaseFixture.Create();

        var catalog = fixture.CreateCatalog();

        Assert.IsNull(catalog.Manifest);
    }

    [TestMethod]
    public void Constructor_WhenSchemaVersionIsNotOne_Throws()
    {
        using var fixture = ReleaseFixture.Create();
        fixture.WriteManifest(fixture.Manifest with { SchemaVersion = 2 });

        var exception = Assert.ThrowsException<InvalidOperationException>(() => fixture.CreateCatalog());

        StringAssert.Contains(exception.Message, "schemaVersion");
    }

    [DataTestMethod]
    [DataRow("missing RID")]
    [DataRow("duplicate RID")]
    [DataRow("duplicate filename")]
    [DataRow("uppercase sha")]
    [DataRow("wrong sha")]
    [DataRow("unsafe filename")]
    [DataRow("non-positive size")]
    public void Constructor_WhenAssetIsInvalid_Throws(string invalidCase)
    {
        using var fixture = ReleaseFixture.Create();
        var assets = fixture.Manifest.Assets.ToArray();
        if (invalidCase == "duplicate RID")
        {
            assets[1] = assets[1] with { Rid = assets[0].Rid };
        }
        else if (invalidCase == "duplicate filename")
        {
            assets[1] = assets[1] with { FileName = assets[0].FileName };
        }
        else
        {
            assets[0] = invalidCase switch
            {
                "missing RID" => assets[0] with { Rid = "unsupported-rid" },
                "uppercase sha" => assets[0] with { Sha256 = new string('A', 64) },
                "wrong sha" => assets[0] with { Sha256 = "not-a-sha256" },
                "unsafe filename" => assets[0] with { FileName = "../escape.tar.gz" },
                "non-positive size" => assets[0] with { Size = 0 },
                _ => throw new ArgumentOutOfRangeException(nameof(invalidCase))
            };
        }
        fixture.WriteManifest(fixture.Manifest with { Assets = assets });

        var exception = Assert.ThrowsException<InvalidOperationException>(() => fixture.CreateCatalog());

        StringAssert.Contains(exception.Message, "invalid asset");
    }

    [TestMethod]
    public void TryGetAssetPath_WhenFileIsTraversalOrNotListed_ReturnsFalse()
    {
        using var fixture = ReleaseFixture.Create();
        fixture.WriteManifest(fixture.Manifest);
        var catalog = fixture.CreateCatalog();

        Assert.IsFalse(catalog.TryGetAssetPath("../manifest.json", out var traversalPath));
        Assert.AreEqual(string.Empty, traversalPath);
        Assert.IsFalse(catalog.TryGetAssetPath("not-listed.tar.gz", out var unlistedPath));
        Assert.AreEqual(string.Empty, unlistedPath);
    }

    [TestMethod]
    public void Reload_WhenNewManifestAppears_UpdatesLatestAndRetainsOlderAssetSelection()
    {
        using var fixture = ReleaseFixture.Create();
        fixture.WriteManifest(fixture.Manifest);
        var catalog = fixture.CreateCatalog();
        var newer = CreateManifest() with
        {
            Version = "1.3.0",
            Assets = SupportedRids.Select(rid => new AgentReleaseAsset(rid,
                $"hypanel-agent-1.3.0-{rid}.{(rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}",
                new string('b', 64), 2)).ToArray()
        };
        fixture.WriteVersionedManifest(fixture.Manifest);
        fixture.WriteManifest(newer);

        catalog.Reload();

        Assert.AreEqual("1.3.0", catalog.Manifest!.Version);
        Assert.AreEqual("hypanel-agent-1.2.3-linux-musl-arm64.tar.gz",
            catalog.FindAsset("1.2.3", "linux-musl-arm64")!.FileName);
        Assert.AreEqual("hypanel-agent-1.3.0-win-arm64.zip",
            catalog.FindAsset("1.3.0", "win-arm64")!.FileName);
    }

    [DataTestMethod]
    [DataRow("linux-x64")]
    [DataRow("linux-arm64")]
    [DataRow("linux-musl-x64")]
    [DataRow("linux-musl-arm64")]
    [DataRow("osx-x64")]
    [DataRow("osx-arm64")]
    [DataRow("win-x64")]
    [DataRow("win-arm64")]
    public void FindAsset_SelectsExactlyOneFrozenRid(string rid)
    {
        using var fixture = ReleaseFixture.Create();
        fixture.WriteManifest(fixture.Manifest);

        var asset = fixture.CreateCatalog().FindAsset("1.2.3", rid);

        Assert.IsNotNull(asset);
        Assert.AreEqual(rid, asset.Rid);
        StringAssert.Contains(asset.FileName, $"-{rid}.");
    }

    private static AgentReleaseManifest CreateManifest() => new(
        1,
        "1.2.3",
        new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
        SupportedRids.Select((rid, index) => new AgentReleaseAsset(
            rid,
            $"hypanel-agent-1.2.3-{rid}.{(rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}",
            string.Create(64, index, static (span, value) => span.Fill("0123456789abcdef"[value % 16])),
            1)).ToArray());

    private sealed class ReleaseFixture : IDisposable
    {
        private ReleaseFixture(string directoryPath, AgentReleaseManifest manifest)
        {
            DirectoryPath = directoryPath;
            Manifest = manifest;
        }

        public string DirectoryPath { get; }

        public AgentReleaseManifest Manifest { get; }

        public static ReleaseFixture Create() => new(
            Directory.CreateTempSubdirectory("HyPanel.Server.Tests-").FullName,
            CreateManifest());

        public void WriteManifest(AgentReleaseManifest manifest)
        {
            var json = JsonSerializer.Serialize(manifest, HyPanelJsonSerializerContext.Default.AgentReleaseManifest);
            File.WriteAllText(Path.Combine(DirectoryPath, "manifest.json"), json);
        }

        public void WriteVersionedManifest(AgentReleaseManifest manifest)
        {
            var json = JsonSerializer.Serialize(manifest, HyPanelJsonSerializerContext.Default.AgentReleaseManifest);
            File.WriteAllText(Path.Combine(DirectoryPath, $"manifest-{manifest.Version}.json"), json);
        }

        public ReleaseCatalog CreateCatalog() => new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyPanel:ReleasesDirectory"] = DirectoryPath
            })
            .Build());

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }
}
