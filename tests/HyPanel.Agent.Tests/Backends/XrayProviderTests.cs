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
    private readonly XrayProvider provider = new(TimeProvider.System);

    private static string WithProxy(string config, string type, string host = "p.webshare.io", int port = 80,
        string? user = "user", string? password = "p@ss") =>
        config.TrimEnd('}') + $",\"upstreamProxyType\":\"{type}\",\"upstreamProxyHost\":\"{host}\",\"upstreamProxyPort\":{port}" +
        (user is null ? "" : $",\"upstreamProxyUsername\":\"{user}\"") +
        (password is null ? "" : $",\"upstreamProxyPassword\":\"{password}\"") + "}";

    private async Task<JsonElement> RenderOutboundsAsync(string config)
    {
        var desired = CreateDesiredState(configJson: config);
        Assert.IsTrue((await provider.ValidateAsync(desired, CancellationToken.None)).IsValid);
        var document = JsonDocument.Parse((await provider.RenderConfigAsync(desired, CancellationToken.None)).Content);
        return document.RootElement.GetProperty("outbounds").Clone();
    }

    [TestMethod]
    public async Task WithoutAnUpstreamProxyTheServiceConnectsDirectly()
    {
        var outbounds = await RenderOutboundsAsync(CreateConfigJson());
        Assert.AreEqual("freedom", outbounds[0].GetProperty("protocol").GetString());
        Assert.AreEqual("direct", outbounds[0].GetProperty("tag").GetString());
    }

    [TestMethod]
    public async Task Socks5UpstreamProxyIsTheDefaultOutboundWithCredentials()
    {
        var outbounds = await RenderOutboundsAsync(WithProxy(CreateConfigJson(), "socks5", port: 1080));
        Assert.AreEqual("socks", outbounds[0].GetProperty("protocol").GetString());
        Assert.AreEqual("upstream", outbounds[0].GetProperty("tag").GetString());
        var server = outbounds[0].GetProperty("settings").GetProperty("servers")[0];
        Assert.AreEqual("p.webshare.io", server.GetProperty("address").GetString());
        Assert.AreEqual(1080, server.GetProperty("port").GetInt32());
        Assert.AreEqual("user", server.GetProperty("users")[0].GetProperty("user").GetString());
        Assert.AreEqual("p@ss", server.GetProperty("users")[0].GetProperty("pass").GetString());
        Assert.IsFalse(outbounds[0].TryGetProperty("streamSettings", out _));
        Assert.AreEqual("blackhole", outbounds[1].GetProperty("protocol").GetString(), "blocked stays available but is not the default");
    }

    [TestMethod]
    public async Task HttpUpstreamProxyWithoutCredentialsOmitsUsers()
    {
        var outbounds = await RenderOutboundsAsync(WithProxy(CreateConfigJson(), "http", user: null, password: null));
        Assert.AreEqual("http", outbounds[0].GetProperty("protocol").GetString());
        Assert.IsFalse(outbounds[0].GetProperty("settings").GetProperty("servers")[0].TryGetProperty("users", out _));
        Assert.IsFalse(outbounds[0].TryGetProperty("streamSettings", out _));
    }

    [TestMethod]
    public async Task HttpsUpstreamProxyWrapsTheConnectionInTls()
    {
        var outbounds = await RenderOutboundsAsync(WithProxy(CreateConfigJson(), "https", port: 443));
        Assert.AreEqual("http", outbounds[0].GetProperty("protocol").GetString());
        var stream = outbounds[0].GetProperty("streamSettings");
        Assert.AreEqual("tls", stream.GetProperty("security").GetString());
        Assert.AreEqual("p.webshare.io", stream.GetProperty("tlsSettings").GetProperty("serverName").GetString());
    }

    [TestMethod]
    [DataRow("{\"upstreamProxyHost\":\"p.webshare.io\",\"upstreamProxyPort\":80}")]
    [DataRow("{\"upstreamProxyType\":\"http\"}")]
    [DataRow("{\"upstreamProxyType\":\"ftp\",\"upstreamProxyHost\":\"p.webshare.io\",\"upstreamProxyPort\":80}")]
    [DataRow("{\"upstreamProxyType\":\"http\",\"upstreamProxyHost\":\"p.webshare.io\",\"upstreamProxyPort\":99999}")]
    public async Task AnIncompleteOrInvalidUpstreamProxyIsRejectedInsteadOfConnectingDirectly(string extra)
    {
        var config = CreateConfigJson().TrimEnd('}') + "," + extra.TrimStart('{');
        var result = await provider.ValidateAsync(CreateDesiredState(configJson: config), CancellationToken.None);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_config", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateAsync_RejectsTheRemovedExitNodeField()
    {
        var config = CreateConfigJson().TrimEnd('}') + $",\"exitNodeId\":\"{Guid.NewGuid()}\"}}";
        var result = await provider.ValidateAsync(CreateDesiredState(configJson: config), CancellationToken.None);
        Assert.IsFalse(result.IsValid);
        Assert.AreEqual("invalid_config", result.ErrorCode);
    }

    [TestMethod]
    public async Task ValidateAsync_AcceptsRelayFieldsOwnedByTheRelayNode()
    {
        var config = CreateConfigJson();
        config = config[..^1] + $",\"relayNodeId\":\"{Guid.NewGuid():D}\",\"relayPort\":9443}}";
        var result = await provider.ValidateAsync(CreateDesiredState(configJson: config), CancellationToken.None);
        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(2, result.TcpPorts.Count);
    }

    [TestMethod]
    public async Task ValidateAsync_ValidConfig_ReturnsTcpPortOnly()
    {
        var result = await provider.ValidateAsync(CreateDesiredState(), CancellationToken.None);

        Assert.IsTrue(result.IsValid);
        Assert.AreEqual(2, result.TcpPorts.Count);
        CollectionAssert.Contains(result.TcpPorts.ToArray(), 443);
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
        Assert.AreEqual("hypanel-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", settings.GetProperty("clients")[0].GetProperty("email").GetString());
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

    [TestMethod]
    [DataRow("unknown", "{\"listenHost\":\"203.0.113.10\",\"listenPort\":443,\"clientId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"clientEmail\":\"client@example\",\"flow\":\"xtls-rprx-vision\",\"realityPrivateKey\":\"AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8\",\"realityPublicKey\":\"ICEiIyQlJicoKSorLC0uLzAxMjM0NTY3ODk6Ozw9Pj8\",\"shortId\":\"a1b2\",\"serverName\":\"www.example.com\",\"destination\":\"www.example.com:443\",\"fingerprint\":\"chrome\",\"extra\":true}")]
    [DataRow("duplicate", "{\"listenHost\":\"203.0.113.10\",\"listenHost\":\"203.0.113.11\"}")]
    [DataRow("malformed", "not-json")]
    public async Task ValidateAsync_StrictJson_IsRejected(string _, string configJson) =>
        Assert.IsFalse((await provider.ValidateAsync(CreateDesiredState(configJson: configJson), CancellationToken.None)).IsValid);

    [TestMethod]
    [DataRow("\"listenHost\":\"example.com\"")]
    [DataRow("\"listenHost\":\"203.0.113.010\"")]
    [DataRow("\"listenPort\":0")]
    [DataRow("\"listenPort\":65536")]
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
        Assert.AreEqual(BackendCapabilities.MultiUser | BackendCapabilities.PerUserTraffic | BackendCapabilities.TrafficStats | BackendCapabilities.Logs | BackendCapabilities.VersionQuery | BackendCapabilities.ConfigValidation, provider.Capabilities);

    [TestMethod]
    public void ParseStats_AttributesOnlyDesiredUsers()
    {
        var userId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var other = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var json = $$"""{"stat":[{"name":"user>>>hypanel-{{userId:N}}>>>traffic>>>uplink","value":123},{"name":"user>>>hypanel-{{userId:N}}>>>traffic>>>downlink","value":456},{"name":"user>>>hypanel-{{other:N}}>>>traffic>>>uplink","value":999}]}""";

        var result = XrayProvider.ParseStats(json, [new BackendUser(userId, ClientId)], DateTimeOffset.UtcNow);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual(userId, result[0].UserId);
        Assert.AreEqual(123L, result[0].UploadBytes);
        Assert.AreEqual(456L, result[0].DownloadBytes);
    }

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

    private static ServiceDesiredState CreateDesiredState(int schemaVersion = 1, string? configJson = null) =>
        new(Guid.NewGuid(), "test", "xray", "25.1.30", true, schemaVersion, configJson ?? CreateConfigJson(),
            [new BackendUser(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), ClientId)], 20000);

    private static string CreateConfigJson(int listenPort = 443, string clientEmail = "client@example", string shortId = "a1b2", string serverName = "www.example.com", string destination = "www.example.com:443") =>
        $$"""{"listenHost":"203.0.113.10","listenPort":{{listenPort}},"flow":"xtls-rprx-vision","realityPrivateKey":"{{PrivateKey}}","realityPublicKey":"{{PublicKey}}","shortId":"{{shortId}}","serverName":"{{serverName}}","destination":"{{destination}}","fingerprint":"chrome"}""";

    private static BackendInstanceContext CreateInstanceContext(string directory, string binaryPath, string configPath) =>
        new(CreateDesiredState(), directory, binaryPath, configPath);
}
