namespace HyPanel.Server.Tests.Endpoints;

using HyPanel.Server.Endpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class EmbeddedWebEndpointsTests
{
    [TestMethod]
    public void EmbeddedWebResources_ContainIndexAndReferencedAssets()
    {
        var assembly = typeof(EmbeddedWebEndpoints).Assembly;
        var resources = assembly.GetManifestResourceNames();

        CollectionAssert.Contains(resources, "HyPanel.Web/index.html");
        Assert.IsTrue(resources.Any(static name => name.StartsWith("HyPanel.Web/assets/index-", StringComparison.Ordinal) &&
                                                   name.EndsWith(".js", StringComparison.Ordinal)));
        Assert.IsTrue(resources.Any(static name => name.StartsWith("HyPanel.Web/assets/index-", StringComparison.Ordinal) &&
                                                   name.EndsWith(".css", StringComparison.Ordinal)));
    }

    [DataTestMethod]
    [DataRow("index-abc.js", true)]
    [DataRow("index_abc.css", true)]
    [DataRow("", false)]
    [DataRow("../secret", false)]
    [DataRow("a..js", false)]
    [DataRow("nested/file.js", false)]
    [DataRow(".hidden", false)]
    public void IsSafeAssetFileName_RejectsTraversalAndNonBasenames(string file, bool expected)
    {
        Assert.AreEqual(expected, EmbeddedWebEndpoints.IsSafeAssetFileName(file));
    }

    [DataTestMethod]
    [DataRow("app.js", "text/javascript; charset=utf-8")]
    [DataRow("app.css", "text/css; charset=utf-8")]
    [DataRow("icon.svg", "image/svg+xml")]
    [DataRow("data.bin", "application/octet-stream")]
    public void GetContentType_ReturnsExplicitSafeMimeType(string file, string expected)
    {
        Assert.AreEqual(expected, EmbeddedWebEndpoints.GetContentType(file));
    }
}
