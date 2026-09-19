using System.Net;
using System.Text;
using HyPanel.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class PublicIpv4ResolverTests
{
    [TestMethod]
    public async Task GetAsync_ValidCanonicalIpv4_ReturnsAndCachesAddress()
    {
        var handler = new StubHandler("203.0.113.42\n");
        var resolver = new PublicIpv4Resolver(new HttpClient(handler), TimeProvider.System);

        Assert.AreEqual("203.0.113.42", await resolver.GetAsync(CancellationToken.None));
        Assert.AreEqual("203.0.113.42", await resolver.GetAsync(CancellationToken.None));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [DataTestMethod]
    [DataRow("2001:db8::1")]
    [DataRow("203.0.113.042")]
    [DataRow("not-an-address")]
    public async Task GetAsync_NonCanonicalIpv4_ReturnsNull(string response)
    {
        var resolver = new PublicIpv4Resolver(new HttpClient(new StubHandler(response)), TimeProvider.System);

        Assert.IsNull(await resolver.GetAsync(CancellationToken.None));
    }

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Assert.AreEqual("https://4.qwq.lu/", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.ASCII, "text/plain")
            });
        }
    }
}
