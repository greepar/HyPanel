using HyPanel.Server.Backends;
using HyPanel.Server.Endpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Endpoints;

[TestClass]
public sealed class MihomoTemplateTests
{
    private static string Quote(string value) => '"' + value + '"';

    [TestMethod]
    public void Render_ExpandsProxiesAndPlaceholderKeepingIndentation()
    {
        const string template = "mode: rule\nproxies: ~\nproxy-groups:\n  - name: PICK\n    proxies:\n      - DIRECT\n      - __ALL_PROXIES__\nrules:\n  - MATCH,PICK\n";

        var output = MihomoTemplate.Render(template, [("a", "  - name: \"a\"\n", SubscriptionGroup.Direct), ("b", "  - name: \"b\"\n", SubscriptionGroup.Relay)], Quote);

        Assert.AreEqual("mode: rule\nproxies:\n  - name: \"a\"\n  - name: \"b\"\nproxy-groups:\n  - name: PICK\n    proxies:\n      - DIRECT\n      - \"a\"\n      - \"b\"\nrules:\n  - MATCH,PICK\n", output);
    }

    [TestMethod]
    public void Render_GroupPlaceholdersTakeTheirOwnLines()
    {
        const string template = "proxy-groups:\n  - name: D\n    proxies:\n      - __DIRECT_PROXIES__\n  - name: V6\n    proxies:\n      - \"__DIRECT_V6_PROXIES__\"\n  - name: R\n    proxies:\n      - __RELAY_PROXIES__\nrules: []\n";

        var output = MihomoTemplate.Render(template,
            [("a", "  - name: \"a\"\n", SubscriptionGroup.Direct), ("b", "  - name: \"b\"\n", SubscriptionGroup.Relay),
             ("c", "  - name: \"c\"\n", SubscriptionGroup.Relay)], Quote);

        StringAssert.Contains(output, "  - name: D\n    proxies:\n      - \"a\"\n  - name: V6\n    proxies:\n      - DIRECT\n" +
                                      "  - name: R\n    proxies:\n      - \"b\"\n      - \"c\"\n");
    }

    [TestMethod]
    public void Render_WithoutProxies_KeepsGroupsValid()
    {
        var output = MihomoTemplate.Render("proxy-groups:\n  - name: X\n    proxies:\n      - __ALL_PROXIES__\nrules: []\n", [], Quote);

        StringAssert.StartsWith(output, "proxies: []\n");
        StringAssert.Contains(output, "      - DIRECT\n");
    }

    [TestMethod]
    public void TryValidate_RequiresGroupsAndRules()
    {
        Assert.IsTrue(MihomoTemplate.TryValidate(HyPanel.Server.Subscriptions.BuiltinProfile.DefaultTemplate, out _));
        Assert.IsFalse(MihomoTemplate.TryValidate("rules:\n  - MATCH,DIRECT\n", out var error));
        StringAssert.Contains(error, "proxy-groups");
        Assert.IsFalse(MihomoTemplate.TryValidate("proxy-groups: []\nrules:\n\t- MATCH,DIRECT\n", out _));
    }
}
