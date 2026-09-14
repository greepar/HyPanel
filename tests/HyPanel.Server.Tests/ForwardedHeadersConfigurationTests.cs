namespace HyPanel.Server.Tests;

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ForwardedHeadersConfigurationTests
{
    [TestMethod]
    public void ConfigureForwardedHeaders_TrustsOnlySingleHopLoopbackProxy()
    {
        var options = new ForwardedHeadersOptions();

        Program.ConfigureForwardedHeaders(options);

        Assert.AreEqual(1, options.ForwardLimit);
        Assert.AreEqual(
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
            options.ForwardedHeaders);
        CollectionAssert.AreEquivalent(
            new[] { IPAddress.Loopback, IPAddress.IPv6Loopback },
            options.KnownProxies.ToArray());
        Assert.AreEqual(0, options.KnownIPNetworks.Count);
    }
}
