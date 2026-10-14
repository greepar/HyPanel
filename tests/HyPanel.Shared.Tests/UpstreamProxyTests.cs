using HyPanel.Shared.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Shared.Tests;

[TestClass]
public sealed class UpstreamProxyTests
{
    private static bool Create(string? type, string? host, int? port, string? user, string? password,
        out UpstreamProxy? proxy, out string? error) => UpstreamProxy.TryCreate(type, host, port, user, password, out proxy, out error);

    [TestMethod]
    public void NothingConfiguredMeansNoProxy()
    {
        Assert.IsTrue(Create(null, null, null, null, null, out var proxy, out var error));
        Assert.IsNull(proxy);
        Assert.IsNull(error);
        Assert.IsTrue(Create("", "", null, "", "", out proxy, out _));
        Assert.IsNull(proxy);
    }

    [TestMethod]
    [DataRow("http")]
    [DataRow("https")]
    [DataRow("socks5")]
    public void EachSupportedTypeIsAccepted(string type)
    {
        Assert.IsTrue(Create(type, "p.webshare.io", 80, "user", "pass", out var proxy, out var error), error);
        Assert.AreEqual(type, proxy!.Type);
        Assert.AreEqual("p.webshare.io", proxy.Host);
        Assert.AreEqual(80, proxy.Port);
    }

    [TestMethod]
    public void CredentialsAreOptional()
    {
        Assert.IsTrue(Create("socks5", "203.0.113.5", 1080, null, null, out var proxy, out _));
        Assert.IsNull(proxy!.Username);
        Assert.IsNull(proxy.Password);
        Assert.IsTrue(Create("http", "203.0.113.5", 8080, "only-user", null, out proxy, out _));
        Assert.AreEqual("only-user", proxy!.Username);
    }

    [TestMethod]
    public void AHalfFilledProxyIsRejectedRatherThanIgnored()
    {
        // Ignoring it would connect directly and leak the node's own IP.
        Assert.IsFalse(Create(null, "p.webshare.io", 80, null, null, out _, out var error));
        StringAssert.Contains(error, "类型");
        Assert.IsFalse(Create("http", null, 80, null, null, out _, out error));
        StringAssert.Contains(error, "地址");
        Assert.IsFalse(Create("http", "p.webshare.io", null, null, null, out _, out error));
        StringAssert.Contains(error, "端口");
        Assert.IsFalse(Create("http", "p.webshare.io", 80, null, "secret", out _, out error));
        StringAssert.Contains(error, "用户名");
    }

    [TestMethod]
    [DataRow("ftp", "p.webshare.io", 80)]
    [DataRow("HTTP", "p.webshare.io", 80)]
    [DataRow("http", "p.webshare.io", 0)]
    [DataRow("http", "p.webshare.io", 65536)]
    [DataRow("http", "bad host", 80)]
    [DataRow("http", "evil.example/path", 80)]
    [DataRow("http", "user@evil.example", 80)]
    [DataRow("http", "[::1]", 80)]
    public void InvalidTypeHostOrPortIsRejected(string type, string host, int port) =>
        Assert.IsFalse(Create(type, host, port, null, null, out _, out _));

    [TestMethod]
    public void ControlCharactersAndOverlongCredentialsAreRejected()
    {
        Assert.IsFalse(Create("http", "p.webshare.io", 80, "us\ner", "x", out _, out _));
        Assert.IsFalse(Create("http", "p.webshare.io", 80, "user", new string('x', 256), out _, out _));
        Assert.IsTrue(Create("http", "p.webshare.io", 80, "user", new string('x', 255), out _, out _));
    }

    [TestMethod]
    public void Ipv6HostIsAcceptedAndBracketedInTheUrl()
    {
        Assert.IsTrue(Create("socks5", "2001:db8::7", 1080, "u", "p", out var proxy, out var error), error);
        Assert.AreEqual("socks5://u:p@[2001:db8::7]:1080", proxy!.ToUrl());
    }

    [TestMethod]
    public void UrlEncodesCredentialsAndUsesTheTypeScheme()
    {
        Assert.IsTrue(Create("https", "p.example", 443, "a:b@c", "p/w d%", out var proxy, out _));
        Assert.AreEqual("https://a%3Ab%40c:p%2Fw%20d%25@p.example:443", proxy!.ToUrl());
        Assert.IsTrue(Create("http", "p.example", 8080, null, null, out proxy, out _));
        Assert.AreEqual("http://p.example:8080", proxy!.ToUrl());
        Assert.IsTrue(proxy.IsHttp);
        Assert.IsTrue(Create("socks5", "p.example", 1080, null, null, out proxy, out _));
        Assert.IsFalse(proxy!.IsHttp);
    }
}
