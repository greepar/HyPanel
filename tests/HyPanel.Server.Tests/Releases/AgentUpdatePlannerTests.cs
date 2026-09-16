using HyPanel.Server.Releases;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Releases;

[TestClass]
public sealed class AgentUpdatePlannerTests
{
    [DataTestMethod]
    [DataRow("Auto", "1.1.0", "1.3.0", null, true)]
    [DataRow("Manual", "1.1.0", "1.3.0", null, false)]
    [DataRow("Auto", "1.3.0", "1.3.0", null, false)]
    [DataRow("Auto", "1.4.0", "1.3.0", null, false)]
    [DataRow("Auto", "1.1.0", "1.3.0-beta.1", null, false)]
    [DataRow("Auto", "1.1.0", "1.3.0", "1.2.0", true)]
    [DataRow("Auto", "1.1.0", "1.3.0", "1.3.0", false)]
    public void ShouldRequestAutoUpdate_EnforcesPolicyStableReleaseAndNoDowngrade(
        string policy, string current, string latest, string? desired, bool expected)
    {
        Assert.AreEqual(expected, AgentUpdatePlanner.ShouldRequestAutoUpdate(policy, current, latest, desired));
    }

    [DataTestMethod]
    [DataRow("1.1.0", "1.3.0", true)]
    [DataRow("1.3.0", "1.3.0", false)]
    [DataRow("1.4.0", "1.3.0", false)]
    [DataRow(null, "1.3.0", false)]
    public void CanRequestManualUpdate_RejectsSameVersionAndDowngrade(string? current, string latest, bool expected)
    {
        Assert.AreEqual(expected, AgentUpdatePlanner.CanRequestManualUpdate(current, latest));
    }
}
