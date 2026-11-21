using System.Text.Json;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Backends.Xray;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class XrayHysteria2ProviderTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid RouteId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private const string Credential = "01234567-89ab-cdef-0123-456789abcdef";
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", "xray-hy2");
    private readonly XrayHysteria2Provider provider =
        new(TimeProvider.System, new BackendInstanceStore(new AgentEnrollmentOptions(null, null, Directory)));

    private static ServiceDesiredState Desired(string extra = "", TlsCertificateAsset? tls = null) => new(
        Guid.NewGuid(), "hy2", "xray-hysteria2", "26.3.27", true, 1,
        $$"""{"listenHost":"0.0.0.0","listenPort":8443,"certificateId":"{{Guid.NewGuid()}}","authPassword":"shared-password","masqueradeUrl":"https://www.bing.com/","upMbps":100,"downMbps":1000{{extra}}}""",
        [new BackendUser(UserId, Credential)], 20000,
        tls ?? new TlsCertificateAsset(Guid.NewGuid(), "fp", "cert", "key"));

    private async Task<JsonElement> RenderAsync(ServiceDesiredState desired)
    {
        Assert.IsTrue((await provider.ValidateAsync(desired, CancellationToken.None)).IsValid);
        var content = (await provider.RenderConfigAsync(desired, CancellationToken.None)).Content;
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    [TestMethod]
    public async Task UsersAuthenticateLikeTheOfficialServer()
    {
        var root = await RenderAsync(Desired());
        var inbound = root.GetProperty("inbounds")[0];

        Assert.AreEqual("hysteria", inbound.GetProperty("protocol").GetString());
        Assert.AreEqual($"hypanel-{UserId:N}:{Credential}",
            inbound.GetProperty("settings").GetProperty("users")[0].GetProperty("auth").GetString());
        var stream = inbound.GetProperty("streamSettings");
        Assert.AreEqual("https://www.bing.com/",
            stream.GetProperty("hysteriaSettings").GetProperty("masquerade").GetProperty("url").GetString());
        Assert.AreEqual("100 mbps", stream.GetProperty("finalmask").GetProperty("quicParams").GetProperty("brutalUp").GetString());
        Assert.IsTrue(Path.IsPathFullyQualified(stream.GetProperty("tlsSettings").GetProperty("certificates")[0]
            .GetProperty("certificateFile").GetString()!), "Xray resolves relative paths against its binary");
    }

    [TestMethod]
    public async Task SalamanderAndLinesAreRendered()
    {
        var root = await RenderAsync(Desired(
            $$""","obfsPassword":"obfs-password","routes":[{"id":"{{RouteId}}","name":"fr","upstreamProxyType":"socks5","upstreamProxyHost":"fr.example","upstreamProxyPort":1080}]"""));
        var inbound = root.GetProperty("inbounds")[0];

        var salamander = inbound.GetProperty("streamSettings").GetProperty("finalmask").GetProperty("udp")[0];
        Assert.AreEqual("salamander", salamander.GetProperty("type").GetString());
        var users = inbound.GetProperty("settings").GetProperty("users");
        Assert.AreEqual(2, users.GetArrayLength());
        Assert.AreEqual($"hypanel-{UserId:N}:{ServiceRoute.Credential(Credential, RouteId)}", users[1].GetProperty("auth").GetString());
        Assert.AreEqual(ServiceRoute.UserTag(UserId, RouteId),
            root.GetProperty("routing").GetProperty("rules")[0].GetProperty("user")[0].GetString());
    }

    [TestMethod]
    public async Task AcmeCertificatesAreRefused()
    {
        var acme = new TlsCertificateAsset(Guid.NewGuid(), "", null, null, TlsCertificateKinds.Acme, AcmeDomains: ["a.example"]);

        Assert.IsFalse((await provider.ValidateAsync(Desired(tls: acme), CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public void PortHoppingReadsTheConfig()
    {
        var hopping = provider.PortHopping(Desired(",\"portHopping\":\"20000-20010\"").ConfigJson);

        Assert.AreEqual(8443, hopping?.ListenPort);
        Assert.AreEqual("20000-20010", hopping?.Ports.ToString());
    }
}
