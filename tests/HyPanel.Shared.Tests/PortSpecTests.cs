using HyPanel.Shared.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Shared.Tests;

[TestClass]
public sealed class PortSpecTests
{
    [TestMethod]
    [DataRow("20000-30000", "20000-30000", 10001)]
    [DataRow(" 443 , 8443,20000-20010 ", "443,8443,20000-20010", 13)]
    [DataRow("20005,20000-20010,20011", "20000-20011", 12)]
    [DataRow("30000:30010，40000", "30000-30010,40000", 12)]
    public void TryParse_NormalizesListsAndRanges(string input, string expected, int count)
    {
        Assert.IsTrue(PortSpec.TryParse(input, out var spec));
        Assert.AreEqual(expected, spec.ToString());
        Assert.AreEqual(count, spec.Count);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("0")]
    [DataRow("65536")]
    [DataRow("30000-20000")]
    [DataRow("abc")]
    [DataRow("1-2-3")]
    public void TryParse_RejectsInvalidInput(string input) => Assert.IsFalse(PortSpec.TryParse(input, out _));
}
