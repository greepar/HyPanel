using HyPanel.Server.Endpoints;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
}
