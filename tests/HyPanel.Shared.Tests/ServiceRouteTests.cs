using System.Text.Json;
using HyPanel.Shared.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Shared.Tests;

[TestClass]
public sealed class ServiceRouteTests
{
    private const string Credential = "01234567-89ab-cdef-0123-456789abcdef";

    [TestMethod]
    public void Credential_IsStablePerRouteAndDiffersBetweenRoutes()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        Assert.AreEqual(ServiceRoute.Credential(Credential, first), ServiceRoute.Credential(Credential, first));
        Assert.AreNotEqual(ServiceRoute.Credential(Credential, first), ServiceRoute.Credential(Credential, second));
        Assert.AreNotEqual(Credential, ServiceRoute.Credential(Credential, first));
        Assert.IsTrue(Guid.TryParseExact(ServiceRoute.Credential(Credential, first), "D", out _));
    }

    [TestMethod]
    [DataRow("""{"routes":[{"id":"00000000-0000-0000-0000-000000000000","name":"a"}]}""", "ID")]
    [DataRow("""{"routes":[{"id":"6f1c0e2a-3b4d-4e5f-8a9b-0c1d2e3f4a5b","name":""}]}""", "名称")]
    [DataRow("""{"routes":[{"id":"6f1c0e2a-3b4d-4e5f-8a9b-0c1d2e3f4a5b","name":"a"},{"id":"7f1c0e2a-3b4d-4e5f-8a9b-0c1d2e3f4a5b","name":"A"}]}""", "重复")]
    [DataRow("""{"routes":[{"id":"6f1c0e2a-3b4d-4e5f-8a9b-0c1d2e3f4a5b","name":"fr","upstreamProxyType":"socks5"}]}""", "fr")]
    public void TryParse_RejectsBadRoutesWithAReadableMessage(string json, string expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.IsFalse(ServiceRoute.TryParse(document.RootElement, out _, out var error));
        StringAssert.Contains(error, expected);
    }

    [TestMethod]
    public void TryParse_ReadsDirectAndProxiedRoutes()
    {
        using var document = JsonDocument.Parse("""{"routes":[{"id":"6f1c0e2a-3b4d-4e5f-8a9b-0c1d2e3f4a5b","name":"直连"},{"id":"7f1c0e2a-3b4d-4e5f-8a9b-0c1d2e3f4a5b","name":"法国","upstreamProxyType":"socks5","upstreamProxyHost":"fr.example","upstreamProxyPort":1080}]}""");

        Assert.IsTrue(ServiceRoute.TryParse(document.RootElement, out var routes, out _));
        Assert.IsNull(routes[0].Proxy);
        Assert.AreEqual("fr.example", routes[1].Proxy!.Host);
    }
}
