using System.Security.Cryptography;
using System.Text;
using HyPanel.Agent.Backends;
using HyPanel.Agent.Backends.Mihomo;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class MihomoProviderTests
{
    private const string Password = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private readonly MihomoProvider provider = new();

    [TestMethod]
    public async Task ValidateAsync_ValidConfig_DeclaresSameTcpAndUdpPort()
    {
        var result = await provider.ValidateAsync(CreateDesiredState(), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        CollectionAssert.AreEqual(new[] { 443 }, result.TcpPorts.ToArray());
        CollectionAssert.AreEqual(new[] { 443 }, result.UdpPorts.ToArray());
    }

    [TestMethod]
    public async Task RenderConfigAsync_ValidConfig_RendersDeterministicMihomoYamlAndSha256()
    {
        var rendered = await provider.RenderConfigAsync(CreateDesiredState(), CancellationToken.None);
        var content = rendered.Content.ToArray();
        var yaml = Encoding.UTF8.GetString(content);

        Assert.AreEqual("config.yaml", rendered.FileName);
        Assert.AreEqual("""
log-level: warning
listeners:
  - name: "hypanel-in"
    type: "shadowsocks"
    port: 443
    listen: "203.0.113.10"
    cipher: "2022-blake3-aes-256-gcm"
    password: "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
    udp: true
rules:
  - "MATCH,DIRECT"
""" + "\n", yaml);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), rendered.Sha256);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(65535)]
    public async Task ValidateAsync_PortBoundariesAndCanonicalIpv6_AreAccepted(int port)
    {
        var result = await provider.ValidateAsync(CreateDesiredState(CreateConfigJson("2001:db8::10", port)), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
    }

    [DataTestMethod]
    [DataRow("unknown", "{\"listenHost\":\"203.0.113.10\",\"listenPort\":443,\"method\":\"2022-blake3-aes-256-gcm\",\"password\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\",\"udp\":true,\"extra\":true}")]
    [DataRow("duplicate", "{\"listenHost\":\"203.0.113.10\",\"listenHost\":\"203.0.113.11\",\"listenPort\":443,\"method\":\"2022-blake3-aes-256-gcm\",\"password\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\",\"udp\":true}")]
    [DataRow("malformed", "not-json")]
    public async Task ValidateAsync_StrictJson_IsRejected(string _, string configJson) =>
        Assert.IsFalse((await provider.ValidateAsync(CreateDesiredState(configJson), CancellationToken.None)).IsValid);

    [DataTestMethod]
    [DataRow("\"listenHost\":\"example.com\"")]
    [DataRow("\"listenHost\":\"203.0.113.010\"")]
    [DataRow("\"listenPort\":0")]
    [DataRow("\"listenPort\":65536")]
    [DataRow("\"method\":\"aes-256-gcm\"")]
    [DataRow("\"password\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\"")]
    [DataRow("\"password\":\"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=\"")]
    [DataRow("\"udp\":false")]
    public async Task ValidateAsync_InvalidFrozenSchemaValues_AreRejected(string replacement)
    {
        var property = replacement.Split(':', 2)[0] + ":";
        var json = CreateConfigJson().Replace(property, replacement, StringComparison.Ordinal);

        Assert.IsFalse((await provider.ValidateAsync(CreateDesiredState(json), CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public void Capabilities_AreExactlyFrozenSet() =>
        Assert.AreEqual(
            BackendCapabilities.Logs | BackendCapabilities.VersionQuery | BackendCapabilities.ConfigValidation,
            provider.Capabilities);

    [TestMethod]
    public async Task CreateProcessSpecAsync_UsesMihomoFileSyntaxAndInstanceWorkingDirectory()
    {
        var instance = CreateInstanceContext("/opt/mihomo", "/opt/mihomo/mihomo", "/opt/mihomo/config.yaml");
        var spec = await provider.CreateProcessSpecAsync(instance, CancellationToken.None);

        Assert.AreEqual("/opt/mihomo/mihomo", spec.FileName);
        CollectionAssert.AreEqual(new[] { "-f", "/opt/mihomo/config.yaml" }, spec.Arguments.ToArray());
        Assert.AreEqual("/opt/mihomo", spec.WorkingDirectory);
        Assert.AreEqual(0, spec.Environment.Count);
    }

    [TestMethod]
    public async Task CheckHealthAsync_UsesInstanceAndConfigFileBoundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hypanel-mihomo-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsFalse((await provider.CheckHealthAsync(
                CreateInstanceContext(directory, "mihomo", Path.Combine(directory, "config.yaml")), CancellationToken.None)).IsHealthy);

            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "config.yaml");
            await File.WriteAllTextAsync(configPath, "listeners: []");

            Assert.IsTrue((await provider.CheckHealthAsync(
                CreateInstanceContext(directory, "mihomo", configPath), CancellationToken.None)).IsHealthy);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CollectTrafficAsync_ReturnsNull() =>
        Assert.IsNull(await provider.CollectTrafficAsync(CreateInstanceContext("/tmp", "/tmp/mihomo", "/tmp/config.yaml"), CancellationToken.None));

    private static ServiceDesiredState CreateDesiredState(string? configJson = null) =>
        new(Guid.NewGuid(), "test", "mihomo", "1.19.0", true, 1, configJson ?? CreateConfigJson());

    private static string CreateConfigJson(string listenHost = "203.0.113.10", int listenPort = 443) =>
        $$"""{"listenHost":"{{listenHost}}","listenPort":{{listenPort}},"method":"2022-blake3-aes-256-gcm","password":"{{Password}}","udp":true}""";

    private static BackendInstanceContext CreateInstanceContext(string directory, string binaryPath, string configPath) =>
        new(CreateDesiredState(), directory, binaryPath, configPath);
}
