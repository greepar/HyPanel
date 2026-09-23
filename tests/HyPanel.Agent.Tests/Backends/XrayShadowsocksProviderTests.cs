using System.Text;
using System.Text.Json;
using HyPanel.Agent.Backends.Xray;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class XrayShadowsocksProviderTests
{
    private const string ServerKey = "AAECAwQFBgcICQoLDA0ODw==";
    private readonly XrayShadowsocksProvider provider = new(TimeProvider.System);

    private static ServiceDesiredState Desired(IReadOnlyList<BackendUser> users, string password = ServerKey) =>
        new(Guid.NewGuid(), "ss", "xray-ss", "26.3.27", true, 1,
            $$"""{"listenHost":"0.0.0.0","listenPort":8388,"password":"{{password}}","method":"2022-blake3-aes-128-gcm","udp":true}""",
            users, 20_001);

    [TestMethod]
    public async Task RenderConfigAsync_RendersOneKeyPerUserAndStatsApi()
    {
        var alice = new BackendUser(Guid.NewGuid(), Guid.NewGuid().ToString("D"));
        var desired = Desired([alice]);

        var validation = await provider.ValidateAsync(desired, CancellationToken.None);
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(
            (await provider.RenderConfigAsync(desired, CancellationToken.None)).Content.Span));

        Assert.IsTrue(validation.IsValid);
        CollectionAssert.AreEqual(new[] { 8388 }, validation.UdpPorts.ToArray());
        var inbound = document.RootElement.GetProperty("inbounds")[0];
        Assert.AreEqual("shadowsocks", inbound.GetProperty("protocol").GetString());
        var settings = inbound.GetProperty("settings");
        Assert.AreEqual("2022-blake3-aes-128-gcm", settings.GetProperty("method").GetString());
        Assert.AreEqual(ServerKey, settings.GetProperty("password").GetString());
        Assert.AreEqual("tcp,udp", settings.GetProperty("network").GetString());
        var client = settings.GetProperty("clients")[0];
        Assert.AreEqual(XrayShadowsocksProvider.UserKey(alice.Credential), client.GetProperty("password").GetString());
        Assert.AreEqual(16, Convert.FromBase64String(client.GetProperty("password").GetString()!).Length);
        Assert.AreEqual($"hypanel-{alice.UserId:N}", client.GetProperty("email").GetString());
        Assert.AreEqual("127.0.0.1:20001", document.RootElement.GetProperty("api").GetProperty("listen").GetString());
    }

    [TestMethod]
    public async Task ValidateAsync_RejectsWrongKeySize()
    {
        var desired = Desired([], password: Convert.ToBase64String(new byte[32]));

        Assert.IsFalse((await provider.ValidateAsync(desired, CancellationToken.None)).IsValid);
    }
}
