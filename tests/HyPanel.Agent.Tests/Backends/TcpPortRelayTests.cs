using System.Net;
using System.Net.Sockets;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class TcpPortRelayTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [TestMethod]
    public async Task Relay_ForwardsBothDirectionsAndStopsWhenRuleIsRemoved()
    {
        using var backend = new TcpListener(IPAddress.Loopback, 0);
        backend.Start();
        var backendPort = ((IPEndPoint)backend.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var connection = await backend.AcceptTcpClientAsync();
            var stream = connection.GetStream();
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer);
            await stream.WriteAsync(buffer.AsMemory(0, read));
            connection.Client.Shutdown(SocketShutdown.Send);
        });
        using var relay = new TcpPortRelay(NullLogger<TcpPortRelay>.Instance);
        var listenPort = FreePort();
        var rule = new PortRelayRule(Guid.NewGuid(), listenPort, "127.0.0.1", backendPort);
        Assert.AreEqual(0, relay.Apply([rule]).Count);
        Assert.AreEqual(1, relay.ListenerCount);

        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, listenPort);
            var stream = client.GetStream();
            await stream.WriteAsync("hello"u8.ToArray());
            var buffer = new byte[64];
            var total = 0;
            while (total < 5)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.AreNotEqual(0, read);
                total += read;
            }
            Assert.AreEqual("hello", System.Text.Encoding.ASCII.GetString(buffer, 0, total));
            // The backend closed its send side, which reaches the client as end of stream.
            Assert.AreEqual(0, await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        await server;

        relay.Apply([]);
        Assert.AreEqual(0, relay.ListenerCount);
        using var after = new TcpClient();
        await Assert.ThrowsExactlyAsync<SocketException>(async () => await after.ConnectAsync(IPAddress.Loopback, listenPort));
    }

    [TestMethod]
    public void Apply_ReportsPortsAlreadyInUseAndIgnoresInvalidRules()
    {
        // Same dual-stack bind as the relay, so the conflict is reported on every platform.
        using var occupied = new TcpListener(IPAddress.IPv6Any, 0);
        occupied.Server.DualMode = true;
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        using var relay = new TcpPortRelay(NullLogger<TcpPortRelay>.Instance);
        var failures = relay.Apply([
            new PortRelayRule(Guid.NewGuid(), port, "127.0.0.1", 9),
            new PortRelayRule(Guid.NewGuid(), 0, "127.0.0.1", 9),
            new PortRelayRule(Guid.NewGuid(), 40000, "", 9)]);
        Assert.AreEqual(1, failures.Count);
        StringAssert.Contains(failures[port], "占用");
        Assert.AreEqual(0, relay.ListenerCount);
    }
}
