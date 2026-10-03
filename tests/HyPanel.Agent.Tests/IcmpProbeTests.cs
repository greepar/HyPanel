using System.Net;
using HyPanel.Agent.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class IcmpProbeTests
{
    [TestMethod]
    public void BuildEcho_HasValidChecksumAndSequence()
    {
        var packet = IcmpProbe.BuildEcho(0x1234, 7);
        Assert.AreEqual(8, packet[0]);
        Assert.AreEqual(7, packet[7]);
        uint sum = 0;
        for (var i = 0; i < packet.Length; i += 2) sum += (uint)(packet[i] << 8 | packet[i + 1]);
        while (sum >> 16 != 0) sum = (sum & 0xffff) + (sum >> 16);
        Assert.AreEqual(0xffffu, sum);
    }

    [TestMethod]
    public void Matches_AcceptsOnlyEchoReplyFromPeerWithMatchingIdAndSequence()
    {
        var peer = IPAddress.Parse("169.254.1.5");
        byte[] icmp = [0, 0, 0, 0, 0x12, 0x34, 0, 7];
        byte[] raw = [.. new byte[] { 0x45, 0, 0, 28, 0, 0, 0, 0, 64, 1, 0, 0, 169, 254, 1, 5, 169, 254, 1, 6 }, .. icmp];
        Assert.IsTrue(IcmpProbe.Matches(raw, true, 0x1234, 7, peer, peer));
        Assert.IsFalse(IcmpProbe.Matches(raw, true, 0x9999, 7, peer, peer));
        Assert.IsFalse(IcmpProbe.Matches(raw, true, 0x1234, 8, peer, peer));
        Assert.IsFalse(IcmpProbe.Matches(raw, true, 0x1234, 7, IPAddress.Parse("10.0.0.1"), peer));
        Assert.IsTrue(IcmpProbe.Matches(icmp, false, 0x9999, 7, peer, peer));
        icmp[0] = 8;
        Assert.IsFalse(IcmpProbe.Matches(icmp, false, 0x1234, 7, peer, peer));
    }
}
