using System.Security.Cryptography;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Agent.Backends.SingBox;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class SingBoxProviderTests
{
    private const string Password = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private readonly SingBoxProvider provider = new();

    [TestMethod]
    public async Task ValidateAsync_ValidConfig_DeclaresSameTcpAndUdpPort()
    {
        var result = await provider.ValidateAsync(CreateDesiredState(), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        CollectionAssert.AreEqual(new[] { 443 }, result.TcpPorts.ToArray());
        CollectionAssert.AreEqual(new[] { 443 }, result.UdpPorts.ToArray());
    }

    [TestMethod]
    public async Task RenderConfigAsync_ValidConfig_RendersSingBox114ProfileDeterministically()
    {
        var rendered = await provider.RenderConfigAsync(CreateDesiredState(), CancellationToken.None);
        using var document = JsonDocument.Parse(rendered.Content);
        var root = document.RootElement;
        var inbound = root.GetProperty("inbounds")[0];

        Assert.AreEqual("config.json", rendered.FileName);
        Assert.AreEqual("warn", root.GetProperty("log").GetProperty("level").GetString());
        Assert.AreEqual("shadowsocks", inbound.GetProperty("type").GetString());
        Assert.AreEqual("hypanel-in", inbound.GetProperty("tag").GetString());
        Assert.AreEqual("203.0.113.10", inbound.GetProperty("listen").GetString());
        Assert.AreEqual(443, inbound.GetProperty("listen_port").GetInt32());
        Assert.AreEqual("2022-blake3-aes-256-gcm", inbound.GetProperty("method").GetString());
        Assert.AreEqual(Password, inbound.GetProperty("password").GetString());
        Assert.IsFalse(inbound.TryGetProperty("network", out _));
        Assert.IsFalse(root.TryGetProperty("route", out _));
        Assert.AreEqual("direct", root.GetProperty("outbounds")[0].GetProperty("type").GetString());
        Assert.AreEqual("direct", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.AreEqual("block", root.GetProperty("outbounds")[1].GetProperty("type").GetString());
        Assert.AreEqual("block", root.GetProperty("outbounds")[1].GetProperty("tag").GetString());
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(rendered.Content.Span)).ToLowerInvariant(), rendered.Sha256);
    }

    [DataTestMethod]
    [DataRow(1)]
    [DataRow(65535)]
    public async Task ValidateAsync_PortBoundariesAndCanonicalIpv6_AreAccepted(int port)
    {
        var config = CreateConfigJson("2001:db8::10", port);

        Assert.IsTrue((await provider.ValidateAsync(CreateDesiredState(configJson: config), CancellationToken.None)).IsValid);
    }

    [DataTestMethod]
    [DataRow("unknown", "{\"listenHost\":\"203.0.113.10\",\"listenPort\":443,\"method\":\"2022-blake3-aes-256-gcm\",\"password\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\",\"udp\":true,\"extra\":true}")]
    [DataRow("duplicate", "{\"listenHost\":\"203.0.113.10\",\"listenHost\":\"203.0.113.11\",\"listenPort\":443,\"method\":\"2022-blake3-aes-256-gcm\",\"password\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\",\"udp\":true}")]
    [DataRow("malformed", "not-json")]
    public async Task ValidateAsync_StrictJson_IsRejected(string _, string configJson) =>
        Assert.IsFalse((await provider.ValidateAsync(CreateDesiredState(configJson: configJson), CancellationToken.None)).IsValid);

    [DataTestMethod]
    [DataRow(2, "")]
    [DataRow(1, "\"listenHost\":\"example.com\"")]
    [DataRow(1, "\"listenHost\":\"203.0.113.010\"")]
    [DataRow(1, "\"listenPort\":0")]
    [DataRow(1, "\"listenPort\":65536")]
    [DataRow(1, "\"method\":\"aes-256-gcm\"")]
    [DataRow(1, "\"password\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\"")]
    [DataRow(1, "\"password\":\"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8=\n\"")]
    [DataRow(1, "\"password\":\"AA==\"")]
    [DataRow(1, "\"udp\":false")]
    public async Task ValidateAsync_InvalidSchemaValues_AreRejected(int schemaVersion, string replacement)
    {
        var configJson = string.IsNullOrEmpty(replacement)
            ? CreateConfigJson()
            : CreateConfigJson().Replace(replacement.Split(':', 2)[0] + ":", replacement, StringComparison.Ordinal);

        Assert.IsFalse((await provider.ValidateAsync(CreateDesiredState(schemaVersion, configJson), CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public void Capabilities_AreExactlyFrozenSet() =>
        Assert.AreEqual(
            BackendCapabilities.Logs | BackendCapabilities.VersionQuery | BackendCapabilities.ConfigValidation,
            provider.Capabilities);

    [TestMethod]
    public async Task CreateProcessSpecAsync_ReturnsExactSingBoxCommand()
    {
        var spec = await provider.CreateProcessSpecAsync(
            CreateInstanceContext("/opt/sing-box", "/opt/sing-box/sing-box", "/opt/sing-box/config.json"),
            CancellationToken.None);

        Assert.AreEqual("/opt/sing-box/sing-box", spec.FileName);
        CollectionAssert.AreEqual(new[] { "run", "-c", "/opt/sing-box/config.json" }, spec.Arguments.ToArray());
        Assert.AreEqual("/opt/sing-box", spec.WorkingDirectory);
        Assert.AreEqual(0, spec.Environment.Count);
    }

    [TestMethod]
    public async Task CheckHealthAsync_UsesInstanceAndConfigFileBoundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hypanel-singbox-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsFalse((await provider.CheckHealthAsync(CreateInstanceContext(directory, "sing-box", Path.Combine(directory, "config.json")), CancellationToken.None)).IsHealthy);
            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(configPath, "{}");
            Assert.IsTrue((await provider.CheckHealthAsync(CreateInstanceContext(directory, "sing-box", configPath), CancellationToken.None)).IsHealthy);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CollectTrafficAsync_ReturnsNull() =>
        Assert.IsNull(await provider.CollectTrafficAsync(CreateInstanceContext("/tmp", "/tmp/sing-box", "/tmp/config.json"), CancellationToken.None));

    private static ServiceDesiredState CreateDesiredState(int schemaVersion = 1, string? configJson = null) =>
        new(Guid.NewGuid(), "test", "sing-box", "1.14.0", true, schemaVersion, configJson ?? CreateConfigJson());

    private static string CreateConfigJson(string listenHost = "203.0.113.10", int listenPort = 443) =>
        $$"""{"listenHost":"{{listenHost}}","listenPort":{{listenPort}},"method":"2022-blake3-aes-256-gcm","password":"{{Password}}","udp":true}""";

    private static BackendInstanceContext CreateInstanceContext(string directory, string binaryPath, string configPath) =>
        new(CreateDesiredState(), directory, binaryPath, configPath);
}
