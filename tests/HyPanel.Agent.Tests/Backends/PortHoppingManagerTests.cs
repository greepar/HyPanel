using HyPanel.Agent.Backends.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class PortHoppingManagerTests
{
    [TestMethod]
    public void BuildScript_ReplacesHyPanelTableAtomically()
    {
        var script = PortHoppingManager.BuildScript([(443, "20000-30000,40000"), (8443, "50000")]);

        StringAssert.StartsWith(script, "add table inet hypanel_hop\ndelete table inet hypanel_hop\ntable inet hypanel_hop {\n");
        StringAssert.Contains(script, "type nat hook prerouting priority dstnat; policy accept;");
        StringAssert.Contains(script, "udp dport { 20000-30000, 40000 } redirect to :443\n");
        StringAssert.Contains(script, "udp dport { 50000 } redirect to :8443\n");
    }

    [TestMethod]
    public void BuildScript_WithoutRules_OnlyRemovesTheTable() =>
        Assert.AreEqual("add table inet hypanel_hop\ndelete table inet hypanel_hop\n", PortHoppingManager.BuildScript([]));
}
