using System.Net;
using System.Net.Sockets;
using HyPanel.Agent.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Networking;

[TestClass]
public sealed class TunnelTcpProbeTests
{
    private static readonly IPAddress Source = IPAddress.Parse("169.254.1.1");
    private static readonly IPEndPoint[] Targets = [new(IPAddress.Parse("192.0.2.1"), 443), new(IPAddress.Parse("192.0.2.2"), 443)];

    [TestMethod]
    public async Task OneReachableTargetIsEnough()
    {
        var result = await TunnelTcpProbe.CheckAsync(Source, TimeSpan.FromSeconds(2), CancellationToken.None, Targets,
            (_, target, _) => target.Address.ToString() == "192.0.2.2"
                ? Task.CompletedTask : throw new SocketException((int)SocketError.TimedOut));
        Assert.IsTrue(result.Success);
    }

    [TestMethod]
    public async Task UnreachableEverywhereMeansTheExitFirewallRejectedForwarding()
    {
        var result = await TunnelTcpProbe.CheckAsync(Source, TimeSpan.FromSeconds(2), CancellationToken.None, Targets,
            (_, _, _) => throw new SocketException((int)SocketError.HostUnreachable));
        Assert.IsFalse(result.Success);
        Assert.IsTrue(result.Rejected);
    }

    [TestMethod]
    public async Task SilenceIsAFailureButNotARejection()
    {
        var result = await TunnelTcpProbe.CheckAsync(Source, TimeSpan.FromMilliseconds(100), CancellationToken.None, Targets,
            async (_, _, ct) => await Task.Delay(TimeSpan.FromSeconds(10), ct));
        Assert.IsFalse(result.Success);
        Assert.IsFalse(result.Rejected);
    }

    [TestMethod]
    public async Task CallerCancellationIsNotSwallowed()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            await TunnelTcpProbe.CheckAsync(Source, TimeSpan.FromSeconds(2), cts.Token, Targets,
                async (_, _, ct) => await Task.Delay(TimeSpan.FromSeconds(10), ct)));
    }
}
