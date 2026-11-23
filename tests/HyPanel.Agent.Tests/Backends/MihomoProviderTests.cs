using System.Text.Json;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Backends.Mihomo;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class MihomoProviderTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private const string Credential = "01234567-89ab-cdef-0123-456789abcdef";
    private const string OtherCredential = "11111111-89ab-cdef-0123-456789abcdef";
    private const string PrivateKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";
    private const string PublicKey = "Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw";
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", "mihomo");
    private readonly MihomoRealityProvider reality = new(null, TimeProvider.System);
    private readonly MihomoAnyTlsProvider anyTls =
        new(null, TimeProvider.System, new BackendInstanceStore(new AgentEnrollmentOptions(null, null, Directory)));

    private static ServiceDesiredState Reality(string extra = "") => new(
        Guid.NewGuid(), "reality", "mihomo-reality", "1.19.34", true, 1,
        $$"""{"listenHost":"0.0.0.0","listenPort":443,"flow":"xtls-rprx-vision","realityPrivateKey":"{{PrivateKey}}","realityPublicKey":"{{PublicKey}}","shortId":"0123456789abcdef","serverName":"www.cloudflare.com","destination":"www.cloudflare.com:443","fingerprint":"chrome"{{extra}}}""",
        [new BackendUser(UserId, Credential), new BackendUser(OtherId, OtherCredential)], 20000);

    private static ServiceDesiredState AnyTls(TlsCertificateAsset? tls = null)
    {
        var certificateId = Guid.NewGuid();
        return new ServiceDesiredState(Guid.NewGuid(), "anytls", "mihomo-anytls", "1.19.34", true, 1,
            $$"""{"listenHost":"0.0.0.0","listenPort":8443,"certificateId":"{{certificateId}}"}""",
            [new BackendUser(UserId, Credential)], 20001,
            tls ?? new TlsCertificateAsset(certificateId, "fp", "cert", "key"));
    }

    private static async Task<JsonElement> RenderAsync(HyPanel.Agent.Backends.IBackendProvider provider, ServiceDesiredState desired)
    {
        var validation = await provider.ValidateAsync(desired, CancellationToken.None);
        Assert.IsTrue(validation.IsValid, validation.ErrorCode);
        var content = (await provider.RenderConfigAsync(desired, CancellationToken.None)).Content;
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    [TestMethod]
    public async Task RealityListenerAdmitsEachUserWithVision()
    {
        var root = await RenderAsync(reality, Reality());
        var listener = root.GetProperty("listeners")[0];

        Assert.AreEqual("vless", listener.GetProperty("type").GetString());
        Assert.AreEqual(443, listener.GetProperty("port").GetInt32());
        var users = listener.GetProperty("users");
        Assert.AreEqual(2, users.GetArrayLength());
        Assert.AreEqual($"hypanel-{UserId:N}", users[0].GetProperty("username").GetString());
        Assert.AreEqual(Credential, users[0].GetProperty("uuid").GetString());
        Assert.AreEqual("xtls-rprx-vision", users[0].GetProperty("flow").GetString());
        var settings = listener.GetProperty("reality-config");
        Assert.AreEqual(PrivateKey, settings.GetProperty("private-key").GetString());
        Assert.AreEqual("www.cloudflare.com:443", settings.GetProperty("dest").GetString());
        Assert.AreEqual("0123456789abcdef", settings.GetProperty("short-id")[0].GetString());
        Assert.AreEqual("DIRECT", settings.GetProperty("proxy").GetString(), "the handshake target never follows the exit");
        Assert.AreEqual("127.0.0.1:20000", root.GetProperty("external-controller").GetString());
        Assert.AreEqual("MATCH,DIRECT", root.GetProperty("rules")[0].GetString());
        Assert.IsTrue(root.GetProperty("ipv6").GetBoolean());
    }

    [TestMethod]
    public async Task UpstreamProxyAndIpv4OnlyAreRendered()
    {
        var root = await RenderAsync(reality, Reality(
            ""","upstreamProxyType":"socks5","upstreamProxyHost":"p.example","upstreamProxyPort":1080,"upstreamProxyUsername":"u","upstreamProxyPassword":"p","ipv4Only":true"""));

        Assert.IsFalse(root.GetProperty("ipv6").GetBoolean());
        Assert.AreEqual("MATCH,upstream", root.GetProperty("rules")[0].GetString());
        var upstream = root.GetProperty("proxies")[0];
        Assert.AreEqual("socks5", upstream.GetProperty("type").GetString());
        Assert.AreEqual("p.example", upstream.GetProperty("server").GetString());
        Assert.AreEqual("u", upstream.GetProperty("username").GetString());
    }

    [TestMethod]
    public async Task RealityReportsTcpListenAndControlPorts()
    {
        var validation = await reality.ValidateAsync(Reality(), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 443, 20000 }, validation.TcpPorts.ToArray());
        Assert.AreEqual(0, validation.UdpPorts.Count);
    }

    [TestMethod]
    public async Task InvalidRealityAndUnknownKeysAreRejected()
    {
        Assert.IsFalse((await reality.ValidateAsync(Reality(",\"fingerprint2\":\"x\""), CancellationToken.None)).IsValid,
            "unknown keys are refused");
        var ios = Reality() with { ConfigJson = Reality().ConfigJson.Replace("\"chrome\"", "\"ios\"") };
        Assert.IsTrue((await reality.ValidateAsync(ios, CancellationToken.None)).IsValid, "every fingerprint the editor offers");
        var randomized = Reality() with { ConfigJson = Reality().ConfigJson.Replace("\"chrome\"", "\"randomized\"") };
        Assert.IsFalse((await reality.ValidateAsync(randomized, CancellationToken.None)).IsValid, "Mihomo clients do not know it");
        var badFlow = Reality() with { ConfigJson = Reality().ConfigJson.Replace("xtls-rprx-vision", "none") };
        Assert.IsFalse((await reality.ValidateAsync(badFlow, CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public async Task AnyTlsUsesTheStoredCertificateAndPasswordPerUser()
    {
        var root = await RenderAsync(anyTls, AnyTls());
        var listener = root.GetProperty("listeners")[0];

        Assert.AreEqual("anytls", listener.GetProperty("type").GetString());
        Assert.AreEqual(Credential, listener.GetProperty("users").GetProperty($"hypanel-{UserId:N}").GetString());
        StringAssert.EndsWith(listener.GetProperty("certificate").GetString(), Path.Combine("tls", "fp", "cert.pem"));
        StringAssert.EndsWith(listener.GetProperty("private-key").GetString(), Path.Combine("tls", "fp", "key.pem"));
    }

    [TestMethod]
    public async Task AnyTlsRefusesAcmeCertificates()
    {
        var acme = new TlsCertificateAsset(Guid.NewGuid(), "", null, null, TlsCertificateKinds.Acme, AcmeDomains: ["a.example"]);

        Assert.IsFalse((await anyTls.ValidateAsync(AnyTls(acme), CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public async Task Shadowsocks2022GivesEachUserADerivedKeyOnTcpAndUdp()
    {
        var provider = new MihomoShadowsocksProvider(null, TimeProvider.System);
        var desired = new ServiceDesiredState(Guid.NewGuid(), "ss", "mihomo-ss2022", "1.19.34", true, 1,
            """{"listenHost":"0.0.0.0","listenPort":8388,"cipher":"2022-blake3-aes-128-gcm","serverKey":"AAECAwQFBgcICQoLDA0ODw=="}""",
            [new BackendUser(UserId, Credential)], 20002);

        var validation = await provider.ValidateAsync(desired, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { 8388 }, validation.UdpPorts.ToArray());
        var listener = (await RenderAsync(provider, desired)).GetProperty("listeners")[0];
        Assert.AreEqual("shadowsocks", listener.GetProperty("type").GetString());
        Assert.AreEqual("AAECAwQFBgcICQoLDA0ODw==", listener.GetProperty("password").GetString());
        Assert.AreEqual(Shadowsocks2022.UserKey(Credential),
            listener.GetProperty("users").GetProperty($"hypanel-{UserId:N}").GetString());
        var wrongLength = desired with { ConfigJson = desired.ConfigJson.Replace("AAECAwQFBgcICQoLDA0ODw==", Convert.ToBase64String(new byte[32])) };
        Assert.IsFalse((await provider.ValidateAsync(wrongLength, CancellationToken.None)).IsValid);
    }

    [TestMethod]
    public void AnyTlsLetsMihomoReadACertificateKeptOnTheNode()
    {
        var certificateId = Guid.NewGuid();
        var desired = AnyTls(new TlsCertificateAsset(certificateId, "", null, null, TlsCertificateKinds.Path,
            "/etc/ssl/hy/fullchain.pem", "/etc/ssl/private/key.pem")) with
        {
            ConfigJson = $$"""{"listenHost":"0.0.0.0","listenPort":8443,"certificateId":"{{certificateId}}"}"""
        };

        var spec = anyTls.CreateProcessSpecAsync(new HyPanel.Agent.Backends.BackendInstanceContext(desired, Directory,
            "/bin/mihomo", Path.Combine(Directory, "config.yaml")), CancellationToken.None).Result;

        Assert.AreEqual(string.Join(Path.PathSeparator, Path.GetFullPath("/etc/ssl/hy"), Path.GetFullPath("/etc/ssl/private")),
            spec.Environment["SAFE_PATHS"]);
        CollectionAssert.AreEqual(new[] { "-d", Directory, "-f", Path.Combine(Directory, "config.yaml") }, spec.Arguments.ToArray());
    }

    [TestMethod]
    public async Task LinesLogInWithDerivedCredentialsAndRouteByUser()
    {
        var route = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var direct = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var root = await RenderAsync(reality, Reality(
            $$""","upstreamProxyType":"http","upstreamProxyHost":"default.example","upstreamProxyPort":8080,"routes":[{"id":"{{route}}","name":"jp","upstreamProxyType":"socks5","upstreamProxyHost":"jp.example","upstreamProxyPort":1080},{"id":"{{direct}}","name":"direct"}]"""));

        var users = root.GetProperty("listeners")[0].GetProperty("users");
        Assert.AreEqual(6, users.GetArrayLength(), "two users on three lines");
        var lineLogin = users.EnumerateArray().Single(item => item.GetProperty("username").GetString() == ServiceRoute.UserTag(UserId, route));
        Assert.AreEqual(ServiceRoute.Credential(Credential, route), lineLogin.GetProperty("uuid").GetString());

        var rules = root.GetProperty("rules").EnumerateArray().Select(item => item.GetString()).ToArray();
        CollectionAssert.AreEqual(new[]
        {
            $"IN-USER,{ServiceRoute.UserTag(UserId, route)}/{ServiceRoute.UserTag(OtherId, route)},route-{route:N}",
            $"IN-USER,{ServiceRoute.UserTag(UserId, direct)}/{ServiceRoute.UserTag(OtherId, direct)},DIRECT",
            "MATCH,upstream"
        }, rules);
        var proxies = root.GetProperty("proxies").EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        CollectionAssert.AreEqual(new[] { "upstream", $"route-{route:N}" }, proxies);
    }

    [TestMethod]
    public async Task Shadowsocks2022LinesGetTheirOwnUserKeys()
    {
        var route = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var provider = new MihomoShadowsocksProvider(null, TimeProvider.System);
        var desired = new ServiceDesiredState(Guid.NewGuid(), "ss", "mihomo-ss2022", "1.19.34", true, 1,
            $$"""{"listenHost":"0.0.0.0","listenPort":8388,"cipher":"2022-blake3-aes-128-gcm","serverKey":"AAECAwQFBgcICQoLDA0ODw==","routes":[{"id":"{{route}}","name":"jp"}]}""",
            [new BackendUser(UserId, Credential)], 20002);

        var map = (await RenderAsync(provider, desired)).GetProperty("listeners")[0].GetProperty("users");

        Assert.AreEqual(Shadowsocks2022.UserKey(ServiceRoute.Credential(Credential, route)),
            map.GetProperty(ServiceRoute.UserTag(UserId, route)).GetString());
    }

    [TestMethod]
    public void TrackerCountsOpenConnectionsAndHandsClosedBytesToTheirUser()
    {
        var tracker = new MihomoTrafficTracker();
        Guid? Of(string? name) => name == "a" ? UserId : name == "b" ? OtherId : null;

        tracker.Observe(Snapshot(100, 1000, ("1", "a", 100, 1000)), Of);
        var totals = tracker.Observe(Snapshot(200, 2000, ("1", "a", 150, 1500), ("2", "b", 50, 500)), Of);
        Assert.AreEqual((150L, 1500L), totals[UserId]);
        Assert.AreEqual((50L, 500L), totals[OtherId]);

        // Connection 1 (user a) closed after another 100/1000 bytes that no poll saw.
        totals = tracker.Observe(Snapshot(320, 3100, ("2", "b", 70, 600)), Of);
        Assert.AreEqual((250L, 2500L), totals[UserId]);
        Assert.AreEqual((70L, 600L), totals[OtherId]);
        Assert.AreEqual(320L, totals.Values.Sum(item => item.Upload), "the node total stays exact");
        Assert.AreEqual(3100L, totals.Values.Sum(item => item.Download));
    }

    [TestMethod]
    public void TrackerKeepsCountingAcrossAMihomoRestart()
    {
        var tracker = new MihomoTrafficTracker();
        Guid? Of(string? name) => name == "a" ? UserId : null;
        tracker.Observe(Snapshot(500, 500, ("1", "a", 500, 500)), Of);

        var totals = tracker.Observe(Snapshot(20, 30, ("1", "a", 20, 30)), Of);

        Assert.AreEqual((520L, 530L), totals[UserId]);
    }

    private static MihomoConnections Snapshot(long upload, long download,
        params (string Id, string User, long Upload, long Download)[] connections) => new()
    {
        UploadTotal = upload,
        DownloadTotal = download,
        Connections = [.. connections.Select(item => new MihomoConnection
        {
            Id = item.Id, Upload = item.Upload, Download = item.Download,
            Metadata = new MihomoConnectionMetadata { InboundUser = item.User }
        })]
    };
}
