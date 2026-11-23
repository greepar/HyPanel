using System.Text;
using System.Text.Json;
using HyPanel.Server.Backends;
using HyPanel.Server.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Backends;

[TestClass]
public sealed class MihomoProtocolTests
{
    private static string Render(string? pinnedSha256, string? serverName)
    {
        var service = new SubscriptionServiceRecord(Guid.NewGuid(), Guid.NewGuid(), "jp · anytls", "mihomo-anytls",
            "{}", "01234567-89ab-cdef-0123-456789abcdef",
            new ServicePublicEndpointRecord(Guid.Empty, "203.0.113.9", 8443, serverName, DateTimeOffset.UtcNow),
            pinnedSha256);
        using var config = JsonDocument.Parse("{}");
        var yaml = new StringBuilder();
        AnyTlsProtocol.Instance.Project(service, config.RootElement, service.Name, service.Credential).AppendMihomo(yaml);
        return yaml.ToString();
    }

    [TestMethod]
    public void EntryCarriesThePasswordAndVerifiesTheCertificate()
    {
        var yaml = Render(null, "hy.example.com");

        StringAssert.Contains(yaml, "  - name: \"jp · anytls\"\n    type: anytls\n    server: \"203.0.113.9\"\n    port: 8443\n");
        StringAssert.Contains(yaml, "    password: \"01234567-89ab-cdef-0123-456789abcdef\"\n");
        StringAssert.Contains(yaml, "    skip-cert-verify: false\n");
        StringAssert.Contains(yaml, "    sni: \"hy.example.com\"\n");
    }

    [TestMethod]
    public void SelfSignedCertificatesArePinned()
    {
        var yaml = Render("ab12", null);

        StringAssert.Contains(yaml, "    skip-cert-verify: true\n    fingerprint: \"ab12\"\n");
        Assert.IsFalse(yaml.Contains("sni:"));
    }

    [TestMethod]
    public void MihomoEnginesShareTheirProtocolsAndBinary()
    {
        var reality = BackendDefinitionCatalog.All.Single(item => item.BackendType == "mihomo-reality");
        var xray = BackendDefinitionCatalog.All.Single(item => item.BackendType == "xray");

        Assert.AreSame(xray.ServiceProtocol, reality.ServiceProtocol, "services switch between Xray and Mihomo");
        CollectionAssert.AreEqual(xray.Fields.Select(item => item.Key).ToArray(), reality.Fields.Select(item => item.Key).ToArray());
        Assert.IsTrue(new[] { "mihomo-reality", "mihomo-anytls", "mihomo-ss2022" }.All(type => BackendDefinitionCatalog.Has(type, BackendFeatures.Routes)));
        Assert.AreEqual("mihomo", HyPanel.Server.Releases.BackendArtifactCatalog.BinarySource("mihomo-anytls"));
        Assert.IsTrue(BackendDefinitionCatalog.Has("mihomo-anytls", BackendFeatures.Certificate));
        Assert.IsFalse(BackendDefinitionCatalog.Has("mihomo-anytls", BackendFeatures.AcmeCertificate));
    }

    [TestMethod]
    public void Shadowsocks2022EntrySendsServerAndUserKeyAndUsesSingMux()
    {
        var credential = "22222222-89ab-cdef-0123-456789abcdef";
        var service = new SubscriptionServiceRecord(Guid.NewGuid(), Guid.NewGuid(), "jp · ss", "mihomo-ss2022",
            "{}", credential, new ServicePublicEndpointRecord(Guid.Empty, "203.0.113.9", 8388, null, DateTimeOffset.UtcNow));
        using var config = JsonDocument.Parse("""{"serverKey":"AAECAwQFBgcICQoLDA0ODw=="}""");
        var yaml = new StringBuilder();
        Shadowsocks2022Protocol.Instance.Project(service, config.RootElement, service.Name, credential)!.AppendMihomo(yaml);

        // The user key the Agent derives; checked against a real Mihomo server.
        StringAssert.Contains(yaml.ToString(), "    password: \"AAECAwQFBgcICQoLDA0ODw==:iwTlvhzlJ+7DX73/MbZM1g==\"\n");
        StringAssert.Contains(yaml.ToString(), "    cipher: 2022-blake3-aes-128-gcm\n");
        StringAssert.Contains(yaml.ToString(), "    udp: true\n    smux:\n      enabled: true\n      protocol: h2mux\n");
        Assert.AreEqual(ListenTransport.Tcp | ListenTransport.Udp, Shadowsocks2022Protocol.Instance.Transport);
        using var missing = JsonDocument.Parse("{}");
        Assert.IsNull(Shadowsocks2022Protocol.Instance.Project(service, missing.RootElement, service.Name, credential));
    }

    [TestMethod]
    public void Shadowsocks2022GeneratesAServerKeyOfTheCipherLength()
    {
        var key = BackendDefinitionCatalog.GenerateDefaults("mihomo-ss2022")["serverKey"];

        Assert.IsTrue(HyPanel.Shared.Networking.Shadowsocks2022.IsKey(key));
        Assert.IsFalse(HyPanel.Shared.Networking.Shadowsocks2022.IsKey(Convert.ToBase64String(new byte[32])));
    }
}
