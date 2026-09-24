using System.Net;
using System.Text;
using HyPanel.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class PublicIpv4ResolverTests
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

    private static string Detail(string ip, string country) =>
        $$"""{"ip":"{{ip}}","family":"v4","country":{{country}},"region":"England","asn":1}""";

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.AreEqual("https://4.qwq.lu/?detail", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
