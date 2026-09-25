using HyPanel.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class PanelMoveTests
{
    [TestMethod]
    [DataRow("https://old.example.com", "https://new.example.com", "https://new.example.com")]
    [DataRow("https://old.example.com", "https://new.example.com:8443/", "https://new.example.com:8443")]
    [DataRow("https://panel.example.com", "https://PANEL.example.com", null)]
    [DataRow("https://panel.example.com/", "https://panel.example.com", null)]
    [DataRow("https://old.example.com", "http://new.example.com", null)]
    [DataRow("https://old.example.com", "not a url", null)]
    [DataRow("https://old.example.com", "https://user:pass@new.example.com", null)]
    public void PanelMoveTarget_OnlyMovesToADifferentHttpsOrigin(string current, string advertised, string? expected) =>
        Assert.AreEqual(expected, SyncWorker.PanelMoveTarget(current, advertised));
}
