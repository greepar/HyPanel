using HyPanel.Server.Endpoints;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Net;

namespace HyPanel.Server.Tests.Endpoints;

[TestClass]
public sealed class AgentSyncEndpointsTests
{
    [TestMethod]
    public void DistinctArtifacts_MultipleServicesUsingSameBackendVersion_ReturnsOneArtifact()
    {
        var artifact = new BackendArtifact("mihomo", "1.19.31", "linux-x64", "mihomo.bin",
            new string('a', 64), 42);

        var result = AgentSyncEndpoints.DistinctArtifacts([artifact, artifact, null]);

        Assert.AreEqual(1, result.Length);
        Assert.AreEqual(artifact, result[0]);
    }

    [DataTestMethod]
    [DataRow("203.0.113.42", "203.0.113.42")]
    [DataRow("::ffff:203.0.113.42", "203.0.113.42")]
    [DataRow("10.0.0.1", null)]
    [DataRow("100.64.0.1", null)]
    [DataRow("172.16.0.1", null)]
    [DataRow("192.168.0.1", null)]
    [DataRow("127.0.0.1", null)]
    [DataRow("2001:db8::1", null)]
    public void PublicIpv4From_ReturnsOnlyPublicIpv4(string input, string? expected) =>
        Assert.AreEqual(expected, AgentSyncEndpoints.PublicIpv4From(IPAddress.Parse(input)));
}
