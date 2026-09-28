using System.Net;
using System.Net.Sockets;
using HyPanel.Agent.Backends.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
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

    [TestMethod]
    public void TryBuildRelayRules_MapsEveryHopPortExceptTheListenPort()
    {
        Assert.IsTrue(PortHoppingManager.TryBuildRelayRules([(443, "443-445,500"), (8443, "600")], out var rules, out var error));

        Assert.IsNull(error);
        CollectionAssert.AreEquivalent(new[] { 444, 445, 500, 600 }, rules.Keys.ToArray());
        Assert.AreEqual(443, rules[500]);
        Assert.AreEqual(8443, rules[600]);
    }

    [TestMethod]
    public void TryBuildRelayRules_RejectsMoreThanTheRelayLimit()
    {
        Assert.IsFalse(PortHoppingManager.TryBuildRelayRules([(443, "20000-30000")], out var rules, out var error));

        Assert.AreEqual(0, rules.Count);
        StringAssert.Contains(error, UdpPortRelay.MaximumPorts.ToString());
    }

    [TestMethod]
    public async Task UdpPortRelay_ForwardsToTheListenPortAndRepliesFromTheHopPort()
    {
        using var service = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var servicePort = ((IPEndPoint)service.Client.LocalEndPoint!).Port;
        var echo = Task.Run(async () =>
        {
            var request = await service.ReceiveAsync();
            await service.SendAsync(request.Buffer.Concat("!"u8.ToArray()).ToArray(), request.RemoteEndPoint);
        });
        var hopPort = FreeUdpPort();
        using var relay = new UdpPortRelay(NullLogger<UdpPortRelay>.Instance);

        Assert.AreEqual(0, relay.Apply(new Dictionary<int, int> { [hopPort] = servicePort }).Count);
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync("hi"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, hopPort));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var reply = await client.ReceiveAsync(timeout.Token);

        CollectionAssert.AreEqual("hi!"u8.ToArray(), reply.Buffer);
        Assert.AreEqual(hopPort, reply.RemoteEndPoint.Port);
        await echo;

        relay.Apply(new Dictionary<int, int>());
        Assert.AreEqual(0, relay.ListenerCount);
    }

    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }
}
