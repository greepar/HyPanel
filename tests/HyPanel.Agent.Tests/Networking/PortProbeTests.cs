using System.Net;
using System.Net.Sockets;
using HyPanel.Agent.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Networking;

[TestClass]
public sealed class PortProbeTests
{
    [TestMethod]
    public void FindTaken_ReportsAListeningTcpPortAndSkipsFreeOnes()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var taken = ((IPEndPoint)listener.LocalEndpoint).Port;

        var holder = PortProbe.FindTaken([taken], [], new HashSet<int>());

        Assert.IsNotNull(holder);
        Assert.AreEqual("tcp", holder.Protocol);
        Assert.AreEqual(taken, holder.Port);
        if (OperatingSystem.IsLinux())
        {
            // Our own process holds it, so /proc identifies it.
            Assert.AreEqual(Environment.ProcessId, holder.ProcessId);
            StringAssert.Contains(holder.Describe(), $"PID {Environment.ProcessId}");
        }
        listener.Stop();
        Assert.IsNull(PortProbe.FindTaken([taken], [], new HashSet<int>()));
    }

    [TestMethod]
    public void FindTaken_ReportsABoundUdpPort()
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
        var taken = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        Assert.AreEqual(taken, PortProbe.FindTaken([], [taken], new HashSet<int>())?.Port);
    }

    [TestMethod]
    public void FindTaken_IgnoresPortsHeldByTheAgentsOwnBackends()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Owner lookup reads /proc.");
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Assert.IsNull(PortProbe.FindTaken([port], [], new HashSet<int> { Environment.ProcessId }));
    }

    [TestMethod]
    public void SocketInodes_ReadsListeningTcpAndBoundUdpEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var tcp = Path.Combine(directory, "tcp");
            File.WriteAllLines(tcp,
            [
                "  sl  local_address rem_address   st tx_queue rx_queue tr tm->when retrnsmt   uid  timeout inode",
                "   0: 00000000:01BB 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 4242 1 0000000000000000 100 0 0 10 0",
                "   1: 0100007F:01BB 0200007F:D431 01 00000000:00000000 00:00000000 00000000     0        0 4343 1 0000000000000000 100 0 0 10 0",
                "   2: 00000000:0050 00000000:0000 0A 00000000:00000000 00:00000000 00000000     0        0 4444 1 0000000000000000 100 0 0 10 0"
            ]);

            CollectionAssert.AreEqual(new[] { "4242" }, PortProbe.SocketInodes(tcp, 443, listeningOnly: true).ToArray(),
                "an established connection on the port is not a listener");
            CollectionAssert.AreEqual(new[] { "4242", "4343" }, PortProbe.SocketInodes(tcp, 443, listeningOnly: false).ToArray());
            Assert.AreEqual(0, PortProbe.SocketInodes(Path.Combine(directory, "missing"), 443, true).Count());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Describe_FallsBackToTheCommandToRunWhenTheProgramIsUnknown()
    {
        StringAssert.Contains(new PortHolder("udp", 443, 812, "nginx").Describe(), "UDP 443 已被 nginx（PID 812）占用");
        StringAssert.Contains(new PortHolder("tcp", 443, null, null).Describe(), "ss -lntup | grep :443");
    }
}
