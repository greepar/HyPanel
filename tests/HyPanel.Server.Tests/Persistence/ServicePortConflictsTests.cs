using HyPanel.Server.Persistence;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Persistence;

[TestClass]
public sealed class ServicePortConflictsTests
{
    private static readonly Guid NodeA = Guid.NewGuid();
    private static readonly Guid NodeB = Guid.NewGuid();

    private static ServiceBindingSource Service(string name, string backend, string config, Guid? node = null, bool enabled = true) =>
        new(Guid.NewGuid(), node ?? NodeA, name, backend, enabled, config);

    [TestMethod]
    public void SameTcpPortOnOneNodeConflicts()
    {
        var reality = Service("reality", "xray", "{\"listenHost\":\"0.0.0.0\",\"listenPort\":42401}");
        var other = Service("Fd", "xray", "{\"listenHost\":\"0.0.0.0\",\"listenPort\":42401}");
        var message = ServicePortConflicts.Find(other, [reality]);
        StringAssert.Contains(message, "reality");
        StringAssert.Contains(message, "42401");
    }

    [TestMethod]
    public void UdpHysteriaAndTcpXrayMayShareAPortNumber()
    {
        var hy2 = Service("hy2", "hysteria2", "{\"listenPort\":443}");
        var xray = Service("xray", "xray", "{\"listenPort\":443}");
        Assert.IsNull(ServicePortConflicts.Find(xray, [hy2]));
    }

    [TestMethod]
    public void ShadowsocksWithUdpTakesBothProtocols()
    {
        var ss = Service("ss", "xray-ss", "{\"listenPort\":8388,\"udp\":true}");
        var hy2 = Service("hy2", "hysteria2", "{\"listenPort\":8388}");
        Assert.IsNotNull(ServicePortConflicts.Find(hy2, [ss]));
        var tcpOnly = Service("ss2", "xray-ss", "{\"listenPort\":8388,\"udp\":false}");
        Assert.IsNull(ServicePortConflicts.Find(hy2, [tcpOnly]));
    }

    [TestMethod]
    public void DifferentNodesAndDisabledServicesNeverConflict()
    {
        var a = Service("a", "xray", "{\"listenPort\":8443}");
        Assert.IsNull(ServicePortConflicts.Find(Service("b", "xray", "{\"listenPort\":8443}", NodeB), [a]));
        var disabled = Service("old", "xray", "{\"listenPort\":8443}", enabled: false);
        Assert.IsNull(ServicePortConflicts.Find(Service("new", "xray", "{\"listenPort\":8443}"), [disabled]));
        // A disabled candidate is never judged: turning something off must always be possible.
        Assert.IsNull(ServicePortConflicts.Find(disabled, [a]));
    }

    [TestMethod]
    public void LoopbackAndWildcardOnTheSamePortOverlapButTwoDistinctHostsDoNot()
    {
        var loopback = Service("lo", "xray", "{\"listenHost\":\"127.0.0.1\",\"listenPort\":9000}");
        Assert.IsNotNull(ServicePortConflicts.Find(Service("any", "xray", "{\"listenHost\":\"0.0.0.0\",\"listenPort\":9000}"), [loopback]));
        var other = Service("lo2", "xray", "{\"listenHost\":\"127.0.0.2\",\"listenPort\":9000}");
        Assert.IsNull(ServicePortConflicts.Find(other, [loopback]));
    }

    [TestMethod]
    public void RelayPortIsATcpListenerOnTheRelayNode()
    {
        var onRelay = Service("local", "xray", "{\"listenPort\":9443}", NodeB);
        var relayed = Service("relayed", "xray", $"{{\"listenPort\":8443,\"relayNodeId\":\"{NodeB}\",\"relayPort\":9443}}");
        StringAssert.Contains(ServicePortConflicts.Find(relayed, [onRelay]), "转发端口 9443");
        // The same number as a UDP listener on the relay node is fine.
        var udp = Service("hy2", "hysteria2", "{\"listenPort\":9443}", NodeB);
        Assert.IsNull(ServicePortConflicts.Find(relayed, [udp]));
        // Two services cannot be relayed through the same port of one node.
        var second = Service("second", "xray", $"{{\"listenPort\":8444,\"relayNodeId\":\"{NodeB}\",\"relayPort\":9443}}");
        Assert.IsNotNull(ServicePortConflicts.Find(second, [relayed]));
    }

    [TestMethod]
    public void ServiceDoesNotConflictWithItselfAndBadConfigIsIgnored()
    {
        var service = Service("s", "xray", "{\"listenPort\":8443}");
        Assert.IsNull(ServicePortConflicts.Find(service, [service]));
        Assert.IsNull(ServicePortConflicts.Find(Service("junk", "xray", "not json"), [service]));
    }
}
