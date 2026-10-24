using System.Text;
using HyPanel.Agent;
using HyPanel.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class UpstreamExitCountryProbeTests
{
    private const string Proxied =
        "{\"listenPort\":443,\"upstreamProxyType\":\"socks5\",\"upstreamProxyHost\":\"p.example\",\"upstreamProxyPort\":1080}";

    [TestMethod]
    public async Task Get_ReportsTheProxyCountryOnceTheBackgroundLookupFinishes()
    {
        var lookups = 0;
        var probe = new UpstreamExitCountryProbe(NullLogger<UpstreamExitCountryProbe>.Instance, TimeProvider.System)
        {
            Lookup = (proxy, _) =>
            {
                Interlocked.Increment(ref lookups);
                Assert.AreEqual("p.example", proxy.Host);
                return Task.FromResult<string?>("FR");
            }
        };
        var proxied = Service(Proxied);
        var direct = Service("{\"listenPort\":444}");

        probe.Get([proxied, direct]);
        await WaitUntil(() => probe.Get([proxied, direct]).Count == 1);

        var countries = probe.Get([proxied, direct]);
        Assert.AreEqual("FR", countries[proxied.ServiceId]);
        Assert.IsFalse(countries.ContainsKey(direct.ServiceId));
        Assert.AreEqual(1, lookups, "a known country is cached");
    }

    [TestMethod]
    public void Get_DisabledServiceOrFailedLookupReportsNothing()
    {
        var probe = new UpstreamExitCountryProbe(NullLogger<UpstreamExitCountryProbe>.Instance, TimeProvider.System)
        {
            Lookup = (_, _) => throw new HttpRequestException("proxy down")
        };

        Assert.AreEqual(0, probe.Get([Service(Proxied) with { Enabled = false }]).Count);
        Assert.AreEqual(0, probe.Get([Service(Proxied)]).Count);
    }

    [TestMethod]
    [DataRow("{\"ip\":\"198.51.100.1\",\"country\":\"fr\"}", "FR")]
    [DataRow("{\"ip\":\"198.51.100.1\",\"country\":null}", null)]
    [DataRow("{\"ip\":\"198.51.100.1\",\"country\":\"FRA\"}", null)]
    [DataRow("not json", null)]
    public void ParseCountry_AcceptsOnlyTwoLetterCodes(string body, string? expected) =>
        Assert.AreEqual(expected, UpstreamExitCountryProbe.ParseCountry(Encoding.UTF8.GetBytes(body)));

    private static ServiceDesiredState Service(string config) =>
        new(Guid.NewGuid(), "svc", "xray", "26.3.27", true, 1, config);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++) await Task.Delay(10);
        Assert.IsTrue(condition());
    }
}
