using HyPanel.Server.Endpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Endpoints;

[TestClass]
public sealed class UpstreamProxyValidationTests
{
    [TestMethod]
    public void NoProxyConfiguredIsFine()
    {
        Assert.IsNull(AdminServicesEndpoints.UpstreamProxyError("{\"listenPort\":443}"));
        Assert.IsNull(AdminServicesEndpoints.UpstreamProxyError("[]"));
    }

    [TestMethod]
    public void ACompleteProxyIsAccepted() => Assert.IsNull(AdminServicesEndpoints.UpstreamProxyError(
        "{\"upstreamProxyType\":\"socks5\",\"upstreamProxyHost\":\"p.webshare.io\",\"upstreamProxyPort\":1080,\"upstreamProxyUsername\":\"u\",\"upstreamProxyPassword\":\"p\"}"));

    [TestMethod]
    public void AHalfFilledProxyIsRejectedWithAnExplanation()
    {
        StringAssert.Contains(AdminServicesEndpoints.UpstreamProxyError("{\"upstreamProxyHost\":\"p.webshare.io\"}"), "类型");
        StringAssert.Contains(AdminServicesEndpoints.UpstreamProxyError(
            "{\"upstreamProxyType\":\"http\",\"upstreamProxyHost\":\"p.webshare.io\"}"), "端口");
        StringAssert.Contains(AdminServicesEndpoints.UpstreamProxyError(
            "{\"upstreamProxyType\":\"http\",\"upstreamProxyHost\":\"p.webshare.io\",\"upstreamProxyPort\":80,\"upstreamProxyPassword\":\"p\"}"), "用户名");
    }

    [TestMethod]
    public void ANonNumericPortIsRejected() => Assert.IsNotNull(AdminServicesEndpoints.UpstreamProxyError(
        "{\"upstreamProxyType\":\"http\",\"upstreamProxyHost\":\"p.webshare.io\",\"upstreamProxyPort\":\"80\"}"));
}
