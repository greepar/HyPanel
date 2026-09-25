namespace HyPanel.Server.Tests;

using HyPanel.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class PanelAddressTests
{
    [TestMethod]
    [DataRow(null, true, null)]
    [DataRow("  ", true, null)]
    [DataRow("https://panel.example.com", true, "https://panel.example.com")]
    [DataRow("https://panel.example.com/", true, "https://panel.example.com")]
    [DataRow("https://panel.example.com:8443", true, "https://panel.example.com:8443")]
    [DataRow("http://panel.example.com", false, null)]
    [DataRow("https://panel.example.com/admin", false, null)]
    [DataRow("https://panel.example.com/?a=1", false, null)]
    [DataRow("https://user@panel.example.com", false, null)]
    [DataRow("panel.example.com", false, null)]
    public void TryNormalize_AcceptsOnlyHttpsOrigins(string? input, bool valid, string? expected)
    {
        Assert.AreEqual(valid, PanelAddress.TryNormalize(input, out var normalized));
        Assert.AreEqual(expected, normalized);
    }
}
