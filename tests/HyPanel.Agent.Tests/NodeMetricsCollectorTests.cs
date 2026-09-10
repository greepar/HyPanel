using HyPanel.Agent;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class NodeMetricsCollectorTests
{
    [TestMethod]
    public void Collect_ContinuousSamplesRemainWithinFrozenBounds()
    {
        var options = new AgentEnrollmentOptions(null, null, Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", Guid.NewGuid().ToString("N")));
        var collector = new NodeMetricsCollector(options, TimeProvider.System);

        for (var sample = 0; sample < 5; sample++)
        {
            var metrics = collector.Collect();

            Assert.IsTrue(double.IsFinite(metrics.CpuUsagePercent));
            Assert.IsTrue(metrics.CpuUsagePercent is >= 0 and <= 100);
            Assert.IsTrue(metrics.UptimeSeconds >= 0);
            Assert.IsTrue(metrics.MemoryTotalBytes >= 0);
            Assert.IsTrue(metrics.MemoryAvailableBytes >= 0);
            Assert.IsTrue(metrics.MemoryAvailableBytes <= metrics.MemoryTotalBytes);
            Assert.IsTrue(metrics.DiskTotalBytes >= 0);
            Assert.IsTrue(metrics.DiskAvailableBytes >= 0);
            Assert.IsTrue(metrics.DiskAvailableBytes <= metrics.DiskTotalBytes);
            Assert.IsTrue(metrics.NetworkUploadBytes >= 0);
            Assert.IsTrue(metrics.NetworkDownloadBytes >= 0);
        }
    }
}
