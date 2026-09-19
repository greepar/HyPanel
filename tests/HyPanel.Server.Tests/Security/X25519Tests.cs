namespace HyPanel.Server.Tests.Security;

using System.Text;
using HyPanel.Server.Backends;
using HyPanel.Server.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class X25519Tests
{
    [TestMethod]
    public void ScalarMultiply_MatchesRfc7748VectorOne()
    {
        var result = X25519.ScalarMultiply(
            Convert.FromHexString("a546e36bf0527c9d3b16154b82465edd62144c0ac1fc5a18506a2244ba449ac4"),
            Convert.FromHexString("e6db6867583030db3594c1a424b15f7c726624ec26b3353b10a903a6d0ab1c4c"));

        Assert.AreEqual("c3da55379de9c6908e94ea4df28d084f32eccf03491c71f754b4075577a28552",
            Convert.ToHexString(result).ToLowerInvariant());
    }

    [TestMethod]
    public void ScalarMultiply_MatchesRfc7748VectorTwo()
    {
        var result = X25519.ScalarMultiply(
            Convert.FromHexString("4b66e9d4d1b4673c5ad22691957d6af5c11b6421e0ea01d42ca4169e7918ba0d"),
            Convert.FromHexString("e5210f12786811d3f4b7959d0538ae2c31dbe7106fc03c3efc4cd549c715a493"));

        Assert.AreEqual("95cbde9476e8907d7aade45cb4b873f88b595a68799fa152e6f8f7647aac7957",
            Convert.ToHexString(result).ToLowerInvariant());
    }

    [TestMethod]
    public void ScalarMultiply_MatchesRfc7748IterationOne()
    {
        var basePoint = new byte[32];
        basePoint[0] = 9;

        var result = X25519.ScalarMultiply(basePoint, basePoint);

        Assert.AreEqual("422c8e7a6227d7bca1350b3e2bb7279f7897b87bb6854b783c60e80311ae3079",
            Convert.ToHexString(result).ToLowerInvariant());
    }

    [TestMethod]
    public void RealityKeyPair_PublicKeyIsDerivedFromPrivateKey()
    {
        var (privateKey, publicKey) = SecretGenerator.RealityKeyPair();
        var scalar = DecodeBase64Url(privateKey);

        Assert.AreEqual(32, scalar.Length);
        Assert.AreEqual(43, privateKey.Length);
        Assert.IsFalse(privateKey.Contains('='), "REALITY keys are unpadded Base64Url.");
        Assert.AreEqual(0, scalar[0] & 7, "Scalar must be clamped like the official x25519 tooling.");
        Assert.AreEqual(0, scalar[31] & 128);
        Assert.AreEqual(64, scalar[31] & 64);
        Assert.AreEqual(ToBase64Url(X25519.ScalarMultiplyBase(scalar)), publicKey);
    }

    [TestMethod]
    public void GenerateDefaults_UsesPerBackendSecretFieldNamesAndUniqueValues()
    {
        var hysteria = BackendDefinitionCatalog.GenerateDefaults("hysteria2");
        Assert.AreEqual(2, hysteria.Count);
        Assert.IsTrue(hysteria.ContainsKey("authPassword"));
        Assert.IsTrue(hysteria.ContainsKey("obfsPassword"));
        Assert.AreNotEqual(hysteria["authPassword"], hysteria["obfsPassword"]);

        var xray = BackendDefinitionCatalog.GenerateDefaults("xray");
        Assert.AreEqual(3, xray.Count);
        Assert.AreEqual(32, DecodeBase64Url(xray["realityPrivateKey"]).Length);
        Assert.AreEqual(16, xray["shortId"].Length);

        var singBox = BackendDefinitionCatalog.GenerateDefaults("sing-box");
        Assert.AreEqual(32, Convert.FromBase64String(singBox["password"]).Length);

        CollectionAssert.AreEqual(Array.Empty<string>(), BackendDefinitionCatalog.GenerateDefaults("unknown").Keys.ToArray());
    }

    [TestMethod]
    public void Catalog_EveryBackendDefinesListenAndConfigKeys()
    {
        var types = BackendDefinitionCatalog.All.Select(item => item.BackendType).ToArray();
        CollectionAssert.AreEqual(new[] { "hysteria2", "xray", "mihomo", "sing-box" }, types);

        foreach (var definition in BackendDefinitionCatalog.All)
        {
            Assert.IsTrue(BackendDefinitionCatalog.TryGet(definition.BackendType, out _));
            Assert.IsTrue(definition.Fields.Any(field => field.Key == "listenHost"), definition.BackendType);
            Assert.IsTrue(definition.Fields.Any(field => field.Key == "listenPort"), definition.BackendType);
            foreach (var field in definition.Fields)
            {
                Assert.AreEqual(field.Key, field.ConfigKey, definition.BackendType);
                Assert.IsTrue(field.Kind is "text" or "password" or "number" or "certificate" or "select" or "fixed",
                    $"{definition.BackendType}.{field.Key} has an unsupported kind '{field.Kind}'.");
            }
        }

        var hysteria = BackendDefinitionCatalog.All.Single(item => item.BackendType == "hysteria2");
        CollectionAssert.AreEquivalent(
            new[] { "listenHost", "listenPort", "certificateId", "authPassword", "masqueradeUrl", "obfsPassword", "upMbps", "downMbps" },
            hysteria.Fields.Select(item => item.Key).ToArray());
        var xray = BackendDefinitionCatalog.All.Single(item => item.BackendType == "xray");
        Assert.AreEqual("xtls-rprx-vision", xray.Fields.Single(item => item.Key == "flow").Fixed);
        var mihomo = BackendDefinitionCatalog.All.Single(item => item.BackendType == "mihomo");
        Assert.AreEqual("2022-blake3-aes-256-gcm", mihomo.Fields.Single(item => item.Key == "method").Fixed);
        Assert.AreEqual("true", mihomo.Fields.Single(item => item.Key == "udp").Fixed);
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        return Convert.FromBase64String(padded);
    }

    private static string ToBase64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
