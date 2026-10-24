using System.Net;
using System.Text;
using HyPanel.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class PublicIpResolverTests
{
    [TestMethod]
    public async Task GetAsync_ValidCanonicalIpv4_ReturnsAndCachesAddressAndCountry()
    {
        var handler = new StubHandler(Detail("203.0.113.42", "\"gb\""));
        var resolver = new PublicIpv4Resolver(new HttpClient(handler), TimeProvider.System);

        Assert.AreEqual("203.0.113.42", await resolver.GetAsync(CancellationToken.None));
        Assert.AreEqual("203.0.113.42", await resolver.GetAsync(CancellationToken.None));
        Assert.AreEqual("GB", resolver.CountryCode);
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public async Task GetAsync_MissingOrInvalidCountry_KeepsAddress()
    {
        foreach (var country in new[] { "null", "\"GBR\"", "\"1A\"" })
        {
            var resolver = new PublicIpv4Resolver(new HttpClient(new StubHandler(Detail("203.0.113.42", country))),
                TimeProvider.System);
            Assert.AreEqual("203.0.113.42", await resolver.GetAsync(CancellationToken.None));
            Assert.IsNull(resolver.CountryCode);
        }
    }

    [TestMethod]
    [DataRow("2001:db8::1")]
    [DataRow("203.0.113.042")]
    [DataRow("not-an-address")]
    public async Task GetAsync_NonCanonicalIpv4_ReturnsNull(string address)
    {
        var resolver = new PublicIpv4Resolver(new HttpClient(new StubHandler(Detail(address, "\"US\""))),
            TimeProvider.System);

        Assert.IsNull(await resolver.GetAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task GetAsync_PlainTextBody_ReturnsNull()
    {
        var resolver = new PublicIpv4Resolver(new HttpClient(new StubHandler("203.0.113.42\n")), TimeProvider.System);

        Assert.IsNull(await resolver.GetAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task GetAsync_Ipv6IsTheLocalAddressTheLookupHostSees()
    {
        // Outgoing IPv6 normally leaves through WARP; only the native address, used as the source, sees itself.
        var native = IPAddress.Parse("2a14:7dc0:102:11f6::4bc7");
        var warp = IPAddress.Parse("2606:4700:110:8058:d4ac:b85d:c297:e61f");
        var resolver = new PublicIpv6Resolver(TimeProvider.System,
            () => [IPAddress.Parse("fe80::1"), warp, native],
            source => new HttpClient(new StubHandler(
                Detail(source.Equals(native) ? native.ToString() : "2a09:bac1:6500:8::3c4:17", "\"SG\""),
                "https://6.qwq.lu/?detail")));

        Assert.AreEqual("2a14:7dc0:102:11f6::4bc7", await resolver.GetAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task GetAsync_Ipv6BehindATunnelOrNatIsNotReported()
    {
        var resolver = new PublicIpv6Resolver(TimeProvider.System,
            () => [IPAddress.Parse("2606:4700:110:8058::1")],
            _ => new HttpClient(new StubHandler(Detail("2a09:bac1:6500:8::3c4:17", "\"SG\""), "https://6.qwq.lu/?detail")));

        Assert.IsNull(await resolver.GetAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task GetAsync_Ipv6IsForgottenAfterADayOfFailures()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-24T00:00:00Z"));
        var handler = new StubHandler(Detail("2a01:4f8::9", "\"DE\""), "https://6.qwq.lu/?detail");
        var resolver = new PublicIpv6Resolver(clock, () => [IPAddress.Parse("2a01:4f8::9")],
            _ => new HttpClient(handler, disposeHandler: false));
        Assert.AreEqual("2a01:4f8::9", await resolver.GetAsync(CancellationToken.None));

        handler.Fail = true;
        clock.Now += TimeSpan.FromHours(7);
        Assert.AreEqual("2a01:4f8::9", await resolver.GetAsync(CancellationToken.None), "a short outage keeps the address");
        clock.Now += TimeSpan.FromHours(18);
        Assert.IsNull(await resolver.GetAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task GetAsync_Ipv4IsKeptThroughFailures()
    {
        var clock = new ManualClock(DateTimeOffset.Parse("2026-10-24T00:00:00Z"));
        var handler = new StubHandler(Detail("203.0.113.42", "\"GB\""));
        var resolver = new PublicIpv4Resolver(new HttpClient(handler), clock);
        Assert.AreEqual("203.0.113.42", await resolver.GetAsync(CancellationToken.None));

        handler.Fail = true;
        clock.Now += TimeSpan.FromDays(3);
        Assert.AreEqual("203.0.113.42", await resolver.GetAsync(CancellationToken.None));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static string Detail(string ip, string country) =>
        $$"""{"ip":"{{ip}}","family":"v4","country":{{country}},"region":"England","asn":1}""";

    private sealed class StubHandler(string response, string uri = "https://4.qwq.lu/?detail") : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public bool Fail { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.AreEqual(uri, request.RequestUri!.AbsoluteUri);
            if (Fail) throw new HttpRequestException("unreachable");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
