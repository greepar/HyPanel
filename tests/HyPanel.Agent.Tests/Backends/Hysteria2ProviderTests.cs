using System.Security.Cryptography;
using System.Text;
using HyPanel.Agent.Backends;
using HyPanel.Agent.Backends.Hysteria2;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class Hysteria2ProviderTests
{
    private static readonly Guid CertificateId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AuthPassword = "auth-secret-123";
    private const string ObfsPassword = "obfs-secret-123";
    private readonly Hysteria2Provider provider = new();

    [TestMethod]
    public async Task ValidateAsync_ValidConfig_ReturnsUdpPortOnly()
    {
        var result = await provider.ValidateAsync(CreateDesiredState(), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        CollectionAssert.AreEqual(Array.Empty<int>(), result.TcpPorts.ToArray());
        CollectionAssert.AreEqual(new[] { 443 }, result.UdpPorts.ToArray());
    }

    [TestMethod]
    public async Task RenderConfigAsync_ValidConfig_RendersExpectedYamlAndSha256()
    {
        var rendered = await provider.RenderConfigAsync(CreateDesiredState(), CancellationToken.None);
        var content = rendered.Content.ToArray();
        var yaml = Encoding.UTF8.GetString(content);

        Assert.AreEqual("config.yaml", rendered.FileName);
        StringAssert.Contains(yaml, "listen: \"203.0.113.10:443\"");
        StringAssert.Contains(yaml, $"  cert: \"tls/{Fingerprint}/cert.pem\"");
        StringAssert.Contains(yaml, $"  key: \"tls/{Fingerprint}/key.pem\"");
        StringAssert.Contains(yaml, "  up: \"100 mbps\"");
        StringAssert.Contains(yaml, "  down: \"200 mbps\"");
        StringAssert.Contains(yaml, "    url: \"https://example.com/\"");
        StringAssert.Contains(yaml, "    password: \"obfs-secret-123\"");
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), rendered.Sha256);
    }

    [TestMethod]
    public async Task RenderConfigAsync_AllowIpv6_UsesBracketedListenAddress()
    {
        var rendered = await provider.RenderConfigAsync(CreateDesiredState(listenHost: "2001:db8::10"), CancellationToken.None);

        StringAssert.Contains(Encoding.UTF8.GetString(rendered.Content.Span), "listen: \"[2001:db8::10]:443\"");
    }

    [TestMethod]
    public async Task RenderConfigAsync_LegacyPersistedPaths_RemainRestorable()
    {
        var json = CreateConfigJson().Replace($"\"certificateId\":\"{CertificateId:D}\"",
            "\"certificatePath\":\"/etc/hysteria/server.crt\",\"privateKeyPath\":\"/etc/hysteria/server.key\"",
            StringComparison.Ordinal);
        var desired = CreateDesiredState(configJson: json) with { TlsCertificate = null };

        var rendered = await provider.RenderConfigAsync(desired, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(rendered.Content.Span);
        StringAssert.Contains(yaml, "cert: \"/etc/hysteria/server.crt\"");
        StringAssert.Contains(yaml, "key: \"/etc/hysteria/server.key\"");
    }

    [TestMethod]
    public async Task RenderConfigAsync_InvalidConfig_DoesNotExposeSecretInException()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await provider.RenderConfigAsync(CreateDesiredState(listenPort: 0), CancellationToken.None));

        Assert.IsFalse(exception.Message.Contains(AuthPassword, StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains(ObfsPassword, StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("unknown property", "{\"listenHost\":\"203.0.113.10\",\"listenPort\":443,\"certificateId\":\"11111111-1111-1111-1111-111111111111\",\"authPassword\":\"auth-secret-123\",\"masqueradeUrl\":\"https://example.com/\",\"upMbps\":100,\"downMbps\":200,\"unexpected\":true}")]
    [DataRow("duplicate property", "{\"listenHost\":\"203.0.113.10\",\"listenHost\":\"203.0.113.11\",\"listenPort\":443,\"certificateId\":\"11111111-1111-1111-1111-111111111111\",\"authPassword\":\"auth-secret-123\",\"masqueradeUrl\":\"https://example.com/\",\"upMbps\":100,\"downMbps\":200}")]
    [DataRow("malformed JSON", "not-json")]
    public async Task ValidateAsync_MalformedOrUnsupportedJson_IsRejected(string _, string json)
    {
        var result = await provider.ValidateAsync(CreateDesiredState(configJson: json), CancellationToken.None);

        Assert.IsFalse(result.IsValid);
    }

    [TestMethod]
    [DataRow("schema", 2, "")]
    [DataRow("hostname", 1, "\"listenHost\":\"example.com\"")]
    [DataRow("port below range", 1, "\"listenPort\":0")]
    [DataRow("port above range", 1, "\"listenPort\":65536")]
    [DataRow("empty certificate", 1, "\"certificateId\":\"00000000-0000-0000-0000-000000000000\"")]
    [DataRow("short auth secret", 1, "\"authPassword\":\"short\"")]
    [DataRow("short obfs secret", 1, "\"obfsPassword\":\"short\"")]
    [DataRow("HTTP masquerade", 1, "\"masqueradeUrl\":\"http://example.com/\"")]
    [DataRow("zero upload bandwidth", 1, "\"upMbps\":0")]
    [DataRow("zero download bandwidth", 1, "\"downMbps\":0")]
    public async Task ValidateAsync_InvalidValues_AreRejected(string _, int schemaVersion, string replacement)
    {
        var configJson = string.IsNullOrEmpty(replacement)
            ? CreateConfigJson()
            : CreateConfigJson().Replace(FindProperty(replacement), replacement, StringComparison.Ordinal);
        var result = await provider.ValidateAsync(CreateDesiredState(schemaVersion: schemaVersion, configJson: configJson), CancellationToken.None);

        Assert.IsFalse(result.IsValid);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(65535)]
    public async Task ValidateAsync_UpperAndLowerValidBoundaries_AreAccepted(int value)
    {
        var json = CreateConfigJson().Replace("\"listenPort\":443", $"\"listenPort\":{value}", StringComparison.Ordinal);
        var result = await provider.ValidateAsync(CreateDesiredState(configJson: json), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task ValidateAsync_MaximumBandwidth_IsAccepted()
    {
        var json = CreateConfigJson()
            .Replace("\"upMbps\":100", "\"upMbps\":100000", StringComparison.Ordinal)
            .Replace("\"downMbps\":200", "\"downMbps\":100000", StringComparison.Ordinal);

        var result = await provider.ValidateAsync(CreateDesiredState(configJson: json), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
    }

    [TestMethod]
    public async Task CreateProcessSpecAsync_ReturnsExactServerCommand()
    {
        var instance = CreateInstanceContext("/opt/hysteria", "/opt/hysteria/hysteria", "/opt/hysteria/config.yaml");

        var spec = await provider.CreateProcessSpecAsync(instance, CancellationToken.None);

        Assert.AreEqual("/opt/hysteria/hysteria", spec.FileName);
        CollectionAssert.AreEqual(new[] { "server", "-c", "/opt/hysteria/config.yaml" }, spec.Arguments.ToArray());
        Assert.AreEqual("/opt/hysteria", spec.WorkingDirectory);
        Assert.AreEqual(0, spec.Environment.Count);
    }

    [TestMethod]
    public async Task RenderConfigAsync_GrantedUsers_RendersUserpassAndLoopbackTrafficStats()
    {
        var alice = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
        var bob = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
        var desired = CreateDesiredState() with
        {
            Users = [new BackendUser(bob, "bob-credential-1"), new BackendUser(alice, "alice-credential")],
            ControlPort = 20_001
        };

        var validation = await provider.ValidateAsync(desired, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString((await provider.RenderConfigAsync(desired, CancellationToken.None)).Content.Span);

        Assert.IsTrue(validation.IsValid);
        CollectionAssert.AreEqual(new[] { 20_001 }, validation.TcpPorts.ToArray());
        StringAssert.Contains(yaml, "  type: \"userpass\"");
        StringAssert.Contains(yaml, $"    \"hypanel-{alice:N}\": \"alice-credential\"");
        StringAssert.Contains(yaml, $"    \"hypanel-{bob:N}\": \"bob-credential-1\"");
        Assert.IsFalse(yaml.Contains(AuthPassword, StringComparison.Ordinal));
        StringAssert.Contains(yaml, "trafficStats:\n  listen: \"127.0.0.1:20001\"");
        Assert.IsTrue(yaml.IndexOf(alice.ToString("N"), StringComparison.Ordinal) <
                      yaml.IndexOf(bob.ToString("N"), StringComparison.Ordinal), "users render in stable order");
    }

    [TestMethod]
    public async Task ValidateAsync_DuplicateOrShortUserCredential_IsRejected()
    {
        var user = Guid.NewGuid();
        var duplicate = CreateDesiredState() with
        {
            Users = [new BackendUser(user, "same-credential"), new BackendUser(Guid.NewGuid(), "same-credential")]
        };
        var tooShort = CreateDesiredState() with { Users = [new BackendUser(user, "short")] };

        Assert.IsFalse((await provider.ValidateAsync(duplicate, CancellationToken.None)).IsValid);
        Assert.IsFalse((await provider.ValidateAsync(tooShort, CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public async Task RenderConfigAsync_PathCertificate_UsesNodeFiles()
    {
        var desired = CreateDesiredState() with
        {
            TlsCertificate = new TlsCertificateAsset(CertificateId, string.Empty, null, null, TlsCertificateKinds.Path,
                "/etc/ssl/hy/fullchain.pem", "/etc/ssl/hy/privkey.pem")
        };

        Assert.IsTrue((await provider.ValidateAsync(desired, CancellationToken.None)).IsValid);
        var yaml = Encoding.UTF8.GetString((await provider.RenderConfigAsync(desired, CancellationToken.None)).Content.Span);
        StringAssert.Contains(yaml, "  cert: \"/etc/ssl/hy/fullchain.pem\"");
        StringAssert.Contains(yaml, "  key: \"/etc/ssl/hy/privkey.pem\"");
    }

    [TestMethod]
    public async Task RenderConfigAsync_AcmeCloudflare_RendersDnsChallengeWithoutTlsFiles()
    {
        var desired = CreateDesiredState() with
        {
            TlsCertificate = new TlsCertificateAsset(CertificateId, string.Empty, null, null, TlsCertificateKinds.Acme,
                AcmeDomains: ["hy.example.com"], AcmeEmail: "ops@example.com",
                AcmeChallenge: AcmeChallenges.Cloudflare, AcmeDnsToken: "cf-token-123")
        };

        var validation = await provider.ValidateAsync(desired, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString((await provider.RenderConfigAsync(desired, CancellationToken.None)).Content.Span);

        Assert.IsTrue(validation.IsValid);
        Assert.IsFalse(yaml.Contains("\ntls:", StringComparison.Ordinal));
        StringAssert.Contains(yaml, "acme:\n  domains:\n    - \"hy.example.com\"");
        StringAssert.Contains(yaml, "  type: \"dns\"");
        StringAssert.Contains(yaml, "      cloudflare_api_token: \"cf-token-123\"");
    }

    [TestMethod]
    public async Task ValidateAsync_AcmeHttpChallenge_ClaimsTcp80AndRejectsMissingToken()
    {
        var http = CreateDesiredState() with
        {
            TlsCertificate = new TlsCertificateAsset(CertificateId, string.Empty, null, null, TlsCertificateKinds.Acme,
                AcmeDomains: ["hy.example.com"], AcmeEmail: "ops@example.com", AcmeChallenge: AcmeChallenges.Http)
        };
        var missingToken = http with { TlsCertificate = http.TlsCertificate! with { AcmeChallenge = AcmeChallenges.Cloudflare } };

        CollectionAssert.Contains((await provider.ValidateAsync(http, CancellationToken.None)).TcpPorts.ToArray(), 80);
        Assert.IsFalse((await provider.ValidateAsync(missingToken, CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public void ParseTraffic_MapsTxToUploadRxToDownloadAndIgnoresUnknownIdentities()
    {
        var alice = Guid.NewGuid();
        var observedAt = DateTimeOffset.Parse("2026-09-23T10:00:00Z");
        var json = "{\"hypanel-" + alice.ToString("N") + "\":{\"tx\":100,\"rx\":900},\"someone-else\":{\"tx\":5,\"rx\":5}}";

        var traffic = Hysteria2Provider.ParseTraffic(json, [new BackendUser(alice, "alice-credential")], observedAt);

        Assert.AreEqual(1, traffic.Count);
        Assert.AreEqual(new BackendUserTraffic(alice, 100, 900, observedAt), traffic[0]);
        Assert.AreEqual(0, Hysteria2Provider.ParseTraffic("not json", [new BackendUser(alice, "x")], observedAt).Count);
    }

    [TestMethod]
    public async Task CollectUserTrafficAsync_ReturnsEmpty()
    {
        var result = await provider.CollectUserTrafficAsync(CreateInstanceContext("/tmp", "/tmp/hysteria", "/tmp/config.yaml"), CancellationToken.None);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public async Task CheckHealthAsync_MissingFiles_ReturnsFalse()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hypanel-hysteria2-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var result = await provider.CheckHealthAsync(CreateInstanceContext(directory, Path.Combine(directory, "hysteria"), Path.Combine(directory, "config.yaml")), CancellationToken.None);

            Assert.IsFalse(result.IsHealthy);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [TestMethod]
    public async Task CheckHealthAsync_ExistingConfig_ReturnsTrue()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hypanel-hysteria2-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var configPath = Path.Combine(directory, "config.yaml");
        await File.WriteAllTextAsync(configPath, "listen: 203.0.113.10:443");
        try
        {
            var result = await provider.CheckHealthAsync(CreateInstanceContext(directory, Path.Combine(directory, "hysteria"), configPath), CancellationToken.None);

            Assert.IsTrue(result.IsHealthy);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static ServiceDesiredState CreateDesiredState(
        int schemaVersion = 1,
        string? listenHost = null,
        int listenPort = 443,
        string? configJson = null) =>
        new(Guid.NewGuid(), "test", "hysteria2", "2.7.1", true, schemaVersion,
            configJson ?? CreateConfigJson(listenHost, listenPort), TlsCertificate: new TlsCertificateAsset(
                CertificateId, Fingerprint, "certificate", "private-key"));

    private static string CreateConfigJson(string? listenHost = null, int listenPort = 443) =>
        $$"""{"listenHost":"{{listenHost ?? "203.0.113.10"}}","listenPort":{{listenPort}},"certificateId":"{{CertificateId:D}}","authPassword":"{{AuthPassword}}","masqueradeUrl":"https://example.com/","obfsPassword":"{{ObfsPassword}}","upMbps":100,"downMbps":200}""";

    private static BackendInstanceContext CreateInstanceContext(string directory, string binaryPath, string configPath) =>
        new(CreateDesiredState(), directory, binaryPath, configPath);

    private static string FindProperty(string replacement) =>
        replacement.Split(':', 2)[0] + ":";

    private static void DeleteDirectory(string directory)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
