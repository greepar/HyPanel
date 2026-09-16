using HyPanel.Shared.Versioning;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Shared.Tests;

[TestClass]
public sealed class SemanticVersionTests
{
    [DataTestMethod]
    [DataRow("1.2.3", "1.2.4", -1)]
    [DataRow("1.2.3-beta.1", "1.2.3-beta.2", -1)]
    [DataRow("1.2.3-beta.2", "1.2.3", -1)]
    [DataRow("1.2.3+build.1", "1.2.3+build.2", 0)]
    [DataRow("2.0.0", "1.99.99", 1)]
    public void CompareTo_UsesSemVerPrecedence(string left, string right, int expectedSign)
    {
        var result = SemanticVersion.Parse(left).CompareTo(SemanticVersion.Parse(right));
        Assert.AreEqual(expectedSign, Math.Sign(result));
    }

    [DataTestMethod]
    [DataRow("")]
    [DataRow("1.2")]
    [DataRow("01.2.3")]
    [DataRow("1.2.3-")]
    [DataRow("1.2.3+bad space")]
    public void TryParse_RejectsInvalidVersions(string value)
    {
        Assert.IsFalse(SemanticVersion.TryParse(value, out _));
    }
}
