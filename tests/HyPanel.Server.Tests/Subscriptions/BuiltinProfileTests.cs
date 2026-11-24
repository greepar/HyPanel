using HyPanel.Server.Subscriptions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Subscriptions;

[TestClass]
public sealed class BuiltinProfileTests
{
    [TestMethod]
    public void Default_HasNodeGroupsCategoriesRulesAndTheirRuleSets()
    {
        var template = BuiltinProfile.DefaultTemplate;

        StringAssert.Contains(template, "  - name: \"✈️ 手动切换\"\n    type: select\n    proxies:\n      - \"🔗 直连节点\"\n      - \"🌐 直连节点 v6\"\n      - \"🔀 代理节点\"\n");
        StringAssert.Contains(template, "  - name: \"🔗 直连节点\"\n    type: select\n    proxies:\n      - __DIRECT_PROXIES__\n");
        StringAssert.Contains(template, "  - \"RULE-SET,Telegram,✈️ Telegram\"\n");
        StringAssert.Contains(template, "  - \"GEOIP,CN,🎯 全球直连,no-resolve\"\n");
        Assert.IsTrue(template.TrimEnd().Contains("  - \"MATCH,🐟 漏网之鱼\"\n\nrule-providers:"), "MATCH is the last rule");
        StringAssert.Contains(template, "  Telegram:\n    type: http\n");
        StringAssert.Contains(template, "    rule-set:OpenAI:\n    - https://1.1.1.1/dns-query#🤖 ChatGPT\n");
        StringAssert.Contains(template, "\nipv6: true\n");
        Assert.IsFalse(template.Contains("__RULE_SET_NAMESERVERS__", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DisabledCategory_LosesItsGroupRulesRuleSetsAndDnsPolicy()
    {
        var template = BuiltinProfile.BuildTemplate(new SubscriptionRules(["telegram", "openai", "direct"], []));

        Assert.IsFalse(template.Contains("✈️ Telegram", StringComparison.Ordinal));
        Assert.IsFalse(template.Contains("  Telegram:", StringComparison.Ordinal), "an unused rule set is not downloaded");
        Assert.IsFalse(template.Contains("🤖 ChatGPT", StringComparison.Ordinal));
        Assert.IsFalse(template.Contains("rule-set:OpenAI", StringComparison.Ordinal));
        StringAssert.Contains(template, "RULE-SET,ChinaDomain,🎯 全球直连", "a required category cannot be turned off");
    }

    [TestMethod]
    public void CustomRules_GoFirstAndAreChecked()
    {
        var rules = new SubscriptionRules([], ["DOMAIN-KEYWORD,qwq.lu,DIRECT", "PROCESS-NAME,rustdesk.exe,🚀 节点选择"]);
        var template = BuiltinProfile.BuildTemplate(rules);
        StringAssert.Contains(template, "\nrules:\n  - \"DOMAIN-KEYWORD,qwq.lu,DIRECT\"\n  - \"PROCESS-NAME,rustdesk.exe,🚀 节点选择\"\n  - \"RULE-SET,LocalAreaNetwork,");

        var policies = BuiltinProfile.Policies(new SubscriptionRules(["telegram"], []));
        Assert.IsTrue(BuiltinProfile.TryValidateRule("DOMAIN-SUFFIX,example.com,REJECT", policies, out _));
        Assert.IsTrue(BuiltinProfile.TryValidateRule("IP-CIDR,10.0.0.0/8,DIRECT,no-resolve", policies, out _));
        Assert.IsFalse(BuiltinProfile.TryValidateRule("DOMAIN,t.me,✈️ Telegram", policies, out var error), "a turned-off group");
        StringAssert.Contains(error, "不存在");
        Assert.IsFalse(BuiltinProfile.TryValidateRule("example.com", policies, out _));
        Assert.IsFalse(BuiltinProfile.TryValidateRule("MATCH,DIRECT", policies, out _));
    }

    [TestMethod]
    [DataRow(null, "Tokyo", "Tokyo", "东京")]
    [DataRow(null, "Asagaya-minami", "Tokyo", "东京")]
    [DataRow(null, null, "Shanghai", "上海")]
    [DataRow(null, "Smallville", "Kansas", "Smallville")]
    [DataRow(null, null, null, null)]
    [DataRow("上海", null, null, "上海")]
    [DataRow(" 大阪 ", "Tokyo", "Tokyo", "大阪")]
    public void DisplayCity_PrefersTheAdminsThenAKnownCityThenTheRegion(string? custom, string? city, string? region,
        string? expected) =>
        Assert.AreEqual(expected, HyPanel.Server.Backends.SubscriptionNames.DisplayCity(custom, city, region));
}
