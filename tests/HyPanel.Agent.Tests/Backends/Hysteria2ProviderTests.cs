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
    private const string CertificatePath = "/etc/hysteria/server.crt";
    private const string PrivateKeyPath = "/etc/hysteria/server.key";
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
        StringAssert.Contains(yaml, "  cert: \"/etc/hysteria/server.crt\"");
        StringAssert.Contains(yaml, "  key: \"/etc/hysteria/server.key\"");
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
    public async Task RenderConfigAsync_InvalidConfig_DoesNotExposeSecretInException()
    {
        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
            await provider.RenderConfigAsync(CreateDesiredState(listenPort: 0), CancellationToken.None));

        Assert.IsFalse(exception.Message.Contains(AuthPassword, StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains(ObfsPassword, StringComparison.Ordinal));
    }

    [DataTestMethod]
    [DataRow("unknown property", "{\"listenHost\":\"203.0.113.10\",\"listenPort\":443,\"certificatePath\":\"/etc/hysteria/server.crt\",\"privateKeyPath\":\"/etc/hysteria/server.key\",\"authPassword\":\"auth-secret-123\",\"masqueradeUrl\":\"https://example.com/\",\"upMbps\":100,\"downMbps\":200,\"unexpected\":true}")]
    [DataRow("duplicate property", "{\"listenHost\":\"203.0.113.10\",\"listenHost\":\"203.0.113.11\",\"listenPort\":443,\"certificatePath\":\"/etc/hysteria/server.crt\",\"privateKeyPath\":\"/etc/hysteria/server.key\",\"authPassword\":\"auth-secret-123\",\"masqueradeUrl\":\"https://example.com/\",\"upMbps\":100,\"downMbps\":200}")]
    [DataRow("malformed JSON", "not-json")]
    public async Task ValidateAsync_MalformedOrUnsupportedJson_IsRejected(string _, string json)
    {
        var result = await provider.ValidateAsync(CreateDesiredState(configJson: json), CancellationToken.None);

        Assert.IsFalse(result.IsValid);
    }

    [DataTestMethod]
    [DataRow("schema", 2, "")]
    [DataRow("hostname", 1, "\"listenHost\":\"example.com\"")]
    [DataRow("port below range", 1, "\"listenPort\":0")]
    [DataRow("port above range", 1, "\"listenPort\":65536")]
    [DataRow("relative certificate path", 1, "\"certificatePath\":\"server.crt\"")]
    [DataRow("relative key path", 1, "\"privateKeyPath\":\"server.key\"")]
    [DataRow("same certificate and key", 1, "\"privateKeyPath\":\"/etc/hysteria/server.crt\"")]
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

    [DataTestMethod]
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
    public async Task CollectTrafficAsync_ReturnsNull()
    {
        var result = await provider.CollectTrafficAsync(CreateInstanceContext("/tmp", "/tmp/hysteria", "/tmp/config.yaml"), CancellationToken.None);

        Assert.IsNull(result);
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
        new(Guid.NewGuid(), "test", "hysteria2", "2.7.1", true, schemaVersion, configJson ?? CreateConfigJson(listenHost, listenPort));

    private static string CreateConfigJson(string? listenHost = null, int listenPort = 443) =>
        $$"""{"listenHost":"{{listenHost ?? "203.0.113.10"}}","listenPort":{{listenPort}},"certificatePath":"{{CertificatePath}}","privateKeyPath":"{{PrivateKeyPath}}","authPassword":"{{AuthPassword}}","masqueradeUrl":"https://example.com/","obfsPassword":"{{ObfsPassword}}","upMbps":100,"downMbps":200}""";

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
