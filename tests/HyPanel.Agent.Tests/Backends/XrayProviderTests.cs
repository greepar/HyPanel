using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Agent.Backends.Xray;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class XrayProviderTests
{
    private const string ClientId = "01234567-89ab-cdef-0123-456789abcdef";
    private const string PrivateKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    private const string PublicKey = "ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8";
    private readonly XrayProvider provider = new();

    [TestMethod]
    public async Task ValidateAsync_ValidConfig_ReturnsTcpPortOnly()
    {
        var result = await provider.ValidateAsync(CreateDesiredState(), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        CollectionAssert.AreEqual(new[] { 443 }, result.TcpPorts.ToArray());
        CollectionAssert.AreEqual(Array.Empty<int>(), result.UdpPorts.ToArray());
    }

    [TestMethod]
    public async Task RenderConfigAsync_ValidConfig_RendersRequiredSemanticFieldsWithoutPublicKey()
    {
        var rendered = await provider.RenderConfigAsync(CreateDesiredState(), CancellationToken.None);
        using var document = JsonDocument.Parse(rendered.Content);
        var root = document.RootElement;
        var inbound = root.GetProperty("inbounds")[0];
        var settings = inbound.GetProperty("settings");
        var reality = inbound.GetProperty("streamSettings").GetProperty("realitySettings");

        Assert.AreEqual("config.json", rendered.FileName);
        Assert.AreEqual("warning", root.GetProperty("log").GetProperty("logLevel").GetString());
        Assert.AreEqual("203.0.113.10", inbound.GetProperty("listen").GetString());
        Assert.AreEqual(443, inbound.GetProperty("port").GetInt32());
        Assert.AreEqual("vless", inbound.GetProperty("protocol").GetString());
        Assert.AreEqual(ClientId, settings.GetProperty("clients")[0].GetProperty("id").GetString());
        Assert.AreEqual("client@example", settings.GetProperty("clients")[0].GetProperty("email").GetString());
        Assert.AreEqual("xtls-rprx-vision", settings.GetProperty("clients")[0].GetProperty("flow").GetString());
        Assert.AreEqual("none", settings.GetProperty("decryption").GetString());
        Assert.AreEqual("tcp", inbound.GetProperty("streamSettings").GetProperty("network").GetString());
        Assert.AreEqual("reality", inbound.GetProperty("streamSettings").GetProperty("security").GetString());
        Assert.IsFalse(reality.GetProperty("show").GetBoolean());
        Assert.AreEqual("www.example.com:443", reality.GetProperty("dest").GetString());
        Assert.AreEqual(0, reality.GetProperty("xver").GetInt32());
        Assert.AreEqual("www.example.com", reality.GetProperty("serverNames")[0].GetString());
        Assert.AreEqual(PrivateKey, reality.GetProperty("privateKey").GetString());
        Assert.AreEqual("a1b2", reality.GetProperty("shortIds")[0].GetString());
        Assert.IsFalse(Encoding.UTF8.GetString(rendered.Content.Span).Contains(PublicKey, StringComparison.Ordinal));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(rendered.Content.Span)).ToLowerInvariant(), rendered.Sha256);
        Assert.AreEqual("freedom", root.GetProperty("outbounds")[0].GetProperty("protocol").GetString());
        Assert.AreEqual("direct", root.GetProperty("outbounds")[0].GetProperty("tag").GetString());
        Assert.AreEqual("blackhole", root.GetProperty("outbounds")[1].GetProperty("protocol").GetString());
        Assert.AreEqual("blocked", root.GetProperty("outbounds")[1].GetProperty("tag").GetString());
    }

    [TestMethod]
    public async Task ValidateAsync_BoundariesAndIpv6Destination_AreAccepted()
    {
        foreach (var port in new[] { 1, 65535 })
        {
            var result = await provider.ValidateAsync(CreateDesiredState(configJson: CreateConfigJson(listenPort: port, destination: "[2001:db8::1]:443")), CancellationToken.None);
            Assert.IsTrue(result.IsValid, $"Port {port} should be valid.");
        }

        var maximumEmail = new string('a', 128);
        var maximumHostname = string.Join('.', new[] { new string('a', 63), new string('b', 63), new string('c', 63), new string('d', 61) });
        var boundaryJson = CreateConfigJson(clientEmail: maximumEmail, shortId: "0011223344556677", serverName: maximumHostname);
        Assert.IsTrue((await provider.ValidateAsync(CreateDesiredState(configJson: boundaryJson), CancellationToken.None)).IsValid);

        var minimumJson = CreateConfigJson(clientEmail: "a", shortId: "00", serverName: "a");
        Assert.IsTrue((await provider.ValidateAsync(CreateDesiredState(configJson: minimumJson), CancellationToken.None)).IsValid);
    }

    [DataTestMethod]
    [DataRow("unknown", "{\"listenHost\":\"203.0.113.10\",\"listenPort\":443,\"clientId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"clientEmail\":\"client@example\",\"flow\":\"xtls-rprx-vision\",\"realityPrivateKey\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\",\"realityPublicKey\":\"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8\",\"shortId\":\"a1b2\",\"serverName\":\"www.example.com\",\"destination\":\"www.example.com:443\",\"fingerprint\":\"chrome\",\"extra\":true}")]
    [DataRow("duplicate", "{\"listenHost\":\"203.0.113.10\",\"listenHost\":\"203.0.113.11\"}")]
    [DataRow("malformed", "not-json")]
    public async Task ValidateAsync_StrictJson_IsRejected(string _, string configJson) =>
        Assert.IsFalse((await provider.ValidateAsync(CreateDesiredState(configJson: configJson), CancellationToken.None)).IsValid);

    [DataTestMethod]
    [DataRow("\"listenHost\":\"example.com\"")]
    [DataRow("\"listenHost\":\"203.0.113.010\"")]
    [DataRow("\"listenPort\":0")]
    [DataRow("\"listenPort\":65536")]
    [DataRow("\"clientId\":\"not-a-uuid\"")]
    [DataRow("\"clientId\":\"01234567-89AB-cdef-0123-456789abcdef\"")]
    [DataRow("\"clientEmail\":\"bad\\nemail\"")]
    [DataRow("\"flow\":\"none\"")]
    [DataRow("\"realityPrivateKey\":\"AA==\"")]
    [DataRow("\"realityPublicKey\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\"")]
    [DataRow("\"shortId\":\"A1\"")]
    [DataRow("\"shortId\":\"a\"")]
    [DataRow("\"serverName\":\"-example.com\"")]
    [DataRow("\"destination\":\"2001:db8::1:443\"")]
    [DataRow("\"destination\":\"example.com:0\"")]
    [DataRow("\"fingerprint\":\"ios\"")]
    public async Task ValidateAsync_InvalidSchemaValues_AreRejected(string replacement)
    {
        var property = replacement.Split(':', 2)[0] + ":";
        var json = CreateConfigJson().Replace(property, replacement, StringComparison.Ordinal);
        Assert.IsFalse((await provider.ValidateAsync(CreateDesiredState(configJson: json), CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public void Capabilities_AreExactlyFrozenSet() =>
        Assert.AreEqual(BackendCapabilities.Users | BackendCapabilities.Logs | BackendCapabilities.VersionQuery | BackendCapabilities.ConfigValidation, provider.Capabilities);

    [TestMethod]
    public async Task CreateProcessSpecAsync_ReturnsExactXrayCommand()
    {
        var instance = CreateInstanceContext("/opt/xray", "/opt/xray/xray", "/opt/xray/config.json");
        var spec = await provider.CreateProcessSpecAsync(instance, CancellationToken.None);

        Assert.AreEqual("/opt/xray/xray", spec.FileName);
        CollectionAssert.AreEqual(new[] { "run", "-c", "/opt/xray/config.json" }, spec.Arguments.ToArray());
        Assert.AreEqual("/opt/xray", spec.WorkingDirectory);
    }

    [TestMethod]
    public async Task CheckHealthAsync_UsesInstanceAndConfigFileBoundary()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hypanel-xray-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Assert.IsFalse((await provider.CheckHealthAsync(CreateInstanceContext(directory, "xray", Path.Combine(directory, "config.json")), CancellationToken.None)).IsHealthy);
            Directory.CreateDirectory(directory);
            var configPath = Path.Combine(directory, "config.json");
            await File.WriteAllTextAsync(configPath, "{}");
            Assert.IsTrue((await provider.CheckHealthAsync(CreateInstanceContext(directory, "xray", configPath), CancellationToken.None)).IsHealthy);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public async Task CollectTrafficAsync_ReturnsNull() =>
        Assert.IsNull(await provider.CollectTrafficAsync(CreateInstanceContext("/tmp", "/tmp/xray", "/tmp/config.json"), CancellationToken.None));

    private static ServiceDesiredState CreateDesiredState(int schemaVersion = 1, string? configJson = null) =>
        new(Guid.NewGuid(), "test", "xray", "25.1.30", true, schemaVersion, configJson ?? CreateConfigJson());

    private static string CreateConfigJson(int listenPort = 443, string clientEmail = "client@example", string shortId = "a1b2", string serverName = "www.example.com", string destination = "www.example.com:443") =>
        $$"""{"listenHost":"203.0.113.10","listenPort":{{listenPort}},"clientId":"{{ClientId}}","clientEmail":"{{clientEmail}}","flow":"xtls-rprx-vision","realityPrivateKey":"{{PrivateKey}}","realityPublicKey":"{{PublicKey}}","shortId":"{{shortId}}","serverName":"{{serverName}}","destination":"{{destination}}","fingerprint":"chrome"}""";

    private static BackendInstanceContext CreateInstanceContext(string directory, string binaryPath, string configPath) =>
        new(CreateDesiredState(), directory, binaryPath, configPath);
}
