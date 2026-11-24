using System.Net;
using HyPanel.Agent;
using HyPanel.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class ProxyTesterTests
{
    private static ProxyTestRequest Request(Guid? id = null, string host = "p.example") =>
        new(id ?? Guid.NewGuid(), "socks5", host, 1080, "user", "secret");

    [TestMethod]
    public async Task Apply_RunsEachTestOnceAndReportsUntilWithdrawn()
    {
        var calls = 0;
        var tester = new ProxyTester(NullLogger<ProxyTester>.Instance)
        {
            Lookup = (proxy, _) =>
            {
                Interlocked.Increment(ref calls);
                Assert.AreEqual("secret", proxy.Password);
                return Task.FromResult(("198.51.100.7", (string?)"FR", (string?)"Paris"));
            }
        };
        var request = Request();
        var finished = new SemaphoreSlim(0);

        tester.Apply([request], () => finished.Release());
        Assert.IsTrue(await finished.WaitAsync(TimeSpan.FromSeconds(5)));
        tester.Apply([request], () => finished.Release());

        var report = tester.Reports().Single();
        Assert.AreEqual((request.TestId, true, "198.51.100.7", "FR", "Paris"),
            (report.TestId, report.Succeeded, report.Ip, report.CountryCode, report.City));
        Assert.AreEqual(1, calls, "a test still listed is not run again");

        tester.Apply([], () => { });
        Assert.AreEqual(0, tester.Reports().Count, "a withdrawn test is forgotten");
    }

    [TestMethod]
    public async Task Run_ReportsFailuresInPlainWords()
    {
        var tester = new ProxyTester(NullLogger<ProxyTester>.Instance)
        {
            Lookup = (_, _) => throw new HttpRequestException("denied", null, HttpStatusCode.ProxyAuthenticationRequired)
        };
        var report = await tester.RunAsync(Request());
        Assert.IsFalse(report.Succeeded);
        Assert.AreEqual("代理拒绝了用户名或密码。", report.Error);

        var invalid = await tester.RunAsync(Request(host: ""));
        Assert.IsFalse(invalid.Succeeded);
        StringAssert.Contains(invalid.Error, "地址");
    }
}
