namespace HyPanel.Server.Tests.Endpoints;

using HyPanel.Server.Endpoints;
using HyPanel.Server.Releases;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ReleaseEndpointsTests
{
    private const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void InstallCode_IsSixDigitsAndCanBeConsumedOnlyOnce()
    {
        var service = new InstallCodeService(TimeProvider.System);
        var code = service.Issue("unix", Token, "https://panel.example", TimeSpan.FromMinutes(15));

        Assert.AreEqual(6, code.Length);
        Assert.IsTrue(code.All(char.IsAsciiDigit));
        Assert.IsTrue(service.TryConsume(code, out var entry));
        Assert.AreEqual("unix", entry.Platform);
        Assert.AreEqual(Token, entry.EnrollmentToken);
        Assert.AreEqual("https://panel.example", entry.BaseUrl);
        Assert.IsFalse(service.TryConsume(code, out _));
    }

    [TestMethod]
    public void InstallCode_RejectsExpiredCode()
    {
        var time = new TestTimeProvider(DateTimeOffset.Parse("2026-09-12T00:00:00Z"));
        var service = new InstallCodeService(time);
        var code = service.Issue("powershell", Token, "https://panel.example", TimeSpan.FromMinutes(15));
        time.Advance(TimeSpan.FromMinutes(15));

        Assert.IsFalse(service.TryConsume(code, out _));
    }

    [TestMethod]
    public void BuildUnixBootstrap_ExportsCredentialsAndLoadsCanonicalInstaller()
    {
        var script = ReleaseEndpoints.BuildUnixBootstrap("https://panel.example", Token);
        StringAssert.Contains(script, "export HYPANEL_PANEL_URL='https://panel.example'");
        StringAssert.Contains(script, $"export HYPANEL_ENROLLMENT_TOKEN='{Token}'");
    }

    [TestMethod]
    public void BuildPowerShellBootstrap_SetsCredentialsAndLoadsCanonicalInstaller()
    {
        var script = ReleaseEndpoints.BuildPowerShellBootstrap("https://panel.example", Token);
        StringAssert.Contains(script, "$env:HYPANEL_PANEL_URL = 'https://panel.example'");
        StringAssert.Contains(script, $"$env:HYPANEL_ENROLLMENT_TOKEN = '{Token}'");
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public void Advance(TimeSpan value) => utcNow += value;
    }
}
