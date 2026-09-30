using System.Diagnostics;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Networking;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Backends;

[TestClass]
public sealed class EgressNetworkManagerTests
{
    [TestMethod]
    public async Task GreUdp_ConfiguresFouOnBothEndsAndCleansReservedPortWhenUnused()
    {
        var id = Guid.NewGuid();
        var source = EgressNetworkManager.BuildScript(new(false,
            [new(id, 1, false, "203.0.113.2", EgressTransports.GreUdp)]));
        var exit = EgressNetworkManager.BuildScript(new(true,
            [new(id, 1, true, "203.0.113.1", EgressTransports.GreUdp)], EgressTransports.GreUdp));
        foreach (var script in new[] { source, exit })
        {
            StringAssert.Contains(script, "ip fou add port 47541 ipproto 47");
            StringAssert.Contains(script, "encap fou encap-sport 47541 encap-dport 47541");
            StringAssert.Contains(script, "mtu 1392 up");
            StringAssert.Contains(script, "udp dport 47541 accept");
            Assert.IsFalse(script.Contains("ip fou del port 47541"));
            if (!OperatingSystem.IsWindows())
            {
                using var process = new Process { StartInfo = new ProcessStartInfo("/bin/sh", "-n")
                    { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false } };
                process.Start();
                await process.StandardInput.WriteAsync(script);
                process.StandardInput.Close();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.AreEqual(0, process.ExitCode, error);
            }
        }
        StringAssert.Contains(exit, "ip saddr 169.254.1.1 masquerade");
        var idleExit = EgressNetworkManager.BuildScript(new(true, [], EgressTransports.GreUdp));
        StringAssert.Contains(idleExit, "ip fou add port 47541 ipproto 47");
        Assert.IsFalse(idleExit.Contains("ip fou del port 47541"));
        StringAssert.Contains(EgressNetworkManager.BuildScript(new(false, [])), "ip fou del port \"$port\"");
        Assert.ThrowsExactly<InvalidOperationException>(() => EgressNetworkManager.BuildScript(new(true,
            [new(id, 1, true, "203.0.113.1", EgressTransports.GreUdp, "{\"port\":65536}")], EgressTransports.GreUdp)));
    }

    [TestMethod]
    public async Task WireGuard_SharedListenerGroupsAllowedAddressesAndUsesSourceRouting()
    {
        var privateKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var publicKey = Convert.ToBase64String(Enumerable.Range(33, 32).Select(i => (byte)i).ToArray());
        var exitOptions = Options(new(4433, "hpwx", privateKey, publicKey));
        var sourceOptions = Options(new(4433, "hpw0123456789ab", privateKey, publicKey));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var exit = EgressNetworkManager.BuildScript(new(true,
            [new(ids[0], 1, true, "203.0.113.1", EgressTransports.WireGuard, exitOptions),
             new(ids[1], 2, true, "203.0.113.1", EgressTransports.WireGuard, exitOptions)], EgressTransports.WireGuard, exitOptions));
        var source = EgressNetworkManager.BuildScript(new(false,
            [new(ids[0], 1, false, "203.0.113.2", EgressTransports.WireGuard, sourceOptions),
             new(ids[1], 2, false, "203.0.113.2", EgressTransports.WireGuard, sourceOptions)]));
        Assert.AreEqual(1, exit.Split("\"$HYPANEL_WG\" syncconf hpwx").Length - 1);
        StringAssert.Contains(exit, "ListenPort = 4433");
        StringAssert.Contains(exit, "AllowedIPs = 169.254.1.1/32, 169.254.1.5/32");
        StringAssert.Contains(exit, "ip saddr != { 169.254.1.1, 169.254.1.5 } drop");
        StringAssert.Contains(exit, "udp dport 4433 accept");
        StringAssert.Contains(source, "Endpoint = 203.0.113.2:4433");
        StringAssert.Contains(source, "PersistentKeepalive = 25");
        Assert.IsFalse(source.Contains("ListenPort ="));
        StringAssert.Contains(source, "unreachable default metric 32760 table 100002");
        StringAssert.Contains(source, "ip saddr 169.254.1.5 oifname != \"hpw0123456789ab\" drop");
        StringAssert.Contains(source, "case \"$slot\" in 1|2) continue");
        foreach (var script in new[] { source, exit })
            await ParseShellAsync(script);
        Assert.ThrowsExactly<InvalidOperationException>(() => EgressNetworkManager.BuildScript(new(true, [],
            EgressTransports.WireGuard, Options(new(4433, "hpwx;bad", privateKey)))));
        Assert.ThrowsExactly<InvalidOperationException>(() => EgressNetworkManager.BuildScript(new(true, [],
            EgressTransports.WireGuard, Options(new(4433, "hpwx", privateKey + "\\n")))));
    }

    [TestMethod]
    public async Task Fou_CustomPortIsAppliedToBothEndsAndPersistedForCleanup()
    {
        var options = "{\"port\":4433}";
        foreach (var isExit in new[] { true, false })
        {
            var script = EgressNetworkManager.BuildScript(new(isExit,
                [new(Guid.NewGuid(), 1, isExit, "203.0.113.2", EgressTransports.GreUdp, options)],
                isExit ? EgressTransports.GreUdp : EgressTransports.Gre, isExit ? options : null));
            StringAssert.Contains(script, "ip fou add port 4433 ipproto 47");
            StringAssert.Contains(script, "encap-sport 4433 encap-dport 4433");
            StringAssert.Contains(script, "udp dport 4433 accept");
            StringAssert.Contains(script, "case \"$port\" in 4433) continue");
            await ParseShellAsync(script);
        }
    }

    private static string Options(WireGuardEgressOptions value) => System.Text.Json.JsonSerializer.Serialize(value,
        HyPanel.Shared.Serialization.HyPanelJsonSerializerContext.Default.WireGuardEgressOptions);

    private static async Task ParseShellAsync(string script)
    {
        if (OperatingSystem.IsWindows()) return;
        using var process = new Process { StartInfo = new ProcessStartInfo("/bin/sh", "-n")
            { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false } };
        process.Start();
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, error);
    }

    [TestMethod]
    public void NewTransport_UsesItsOwnCommandsAndMtuWithoutChangingRoutingOrNat()
    {
        var registry = new EgressTransportRegistry([new GreEgressTransportBackend(), new TestUdpBackend()]);
        var state = new EgressNetworkState(true,
            [new(Guid.NewGuid(), 1, true, "203.0.113.1", "test-udp", "{\"port\":5555}")], "test-udp");
        var script = EgressNetworkManager.BuildScriptCore(state, registry);
        StringAssert.Contains(script, "prepare-test-udp");
        StringAssert.Contains(script, "create-test-udp-hpe1");
        StringAssert.Contains(script, "mtu 1280 up");
        StringAssert.Contains(script, "maxseg size set 1240");
        StringAssert.Contains(script, "udp dport 5555 accept");
        StringAssert.Contains(script, "masquerade");
        Assert.IsFalse(script.Contains("ip tunnel add"));
        Assert.IsFalse(script.Contains("ip protocol gre"));
        CollectionAssert.AreEquivalent(new[] { "gre", "test-udp" }, registry.SupportedTransports.ToArray());
    }

    [TestMethod]
    public void ReservedButUnimplementedTransports_AreRejectedAndNeverFallBackToGre()
    {
        foreach (var id in new[] { EgressTransports.GretapOverUdp })
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => EgressNetworkManager.BuildScript(new(true, [], id)));
            Assert.ThrowsExactly<InvalidOperationException>(() => EgressNetworkManager.BuildScript(new(false,
                [new(Guid.NewGuid(), 1, false, "203.0.113.1", id)])));
        }
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new EgressTransportRegistry([new GreEgressTransportBackend(), new GreEgressTransportBackend()]));
    }

    private sealed class TestUdpBackend : IEgressTransportBackend
    {
        public EgressTransportDefinition Definition => new("test-udp", "Test UDP", false, "udp", null, 1280);
        public string PrepareExit(string? optionsJson) => "true # prepare-test-udp\n";
        public string ConfigureTunnel(EgressTunnel tunnel)
        {
            Assert.AreEqual("{\"port\":5555}", tunnel.TransportOptionsJson);
            return $"true # create-test-udp-{EgressAddressing.Interface(tunnel.Slot)}\n";
        }
        public string InputFirewallRule(EgressTunnel tunnel) => $"ip saddr {tunnel.RemoteIpv4} udp dport 5555 accept";
    }

    [TestMethod]
    public async Task SourceAndExitScripts_ParseAndScopeRoutesAndNat()
    {
        var id = Guid.NewGuid();
        var source = EgressNetworkManager.BuildScript(new(false, [new(id, 1, false, "203.0.113.2")]));
        var exit = EgressNetworkManager.BuildScript(new(true, [new(id, 1, true, "203.0.113.1")]));
        StringAssert.Contains(source, "ip route replace unreachable default metric 32760 table 100001");
        StringAssert.Contains(source, $"from {EgressAddressing.Source(1)}/32 table 100001");
        Assert.IsFalse(source.Contains("net.ipv4.ip_forward=1"));
        Assert.IsFalse(source.Contains("masquerade"));
        StringAssert.Contains(source, $"ip saddr {EgressAddressing.Source(1)} oifname != \"hpe1\" drop");
        Assert.IsFalse(source.Contains($"iifname \"hpe1\" ip saddr != {EgressAddressing.Exit(1)} drop"));
        StringAssert.Contains(exit, $"iifname \"hpe1\" ip saddr {EgressAddressing.Source(1)} masquerade");
        Assert.IsFalse(exit.Contains("ip rule add"));
        Assert.IsFalse(exit.Contains("flush ruleset"));
        if (!OperatingSystem.IsWindows())
        {
            foreach (var script in new[] { source, exit, EgressNetworkManager.BuildScript(new(false, [])) })
            {
                using var process = new Process { StartInfo = new ProcessStartInfo("/bin/sh", "-n")
                    { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false } };
                process.Start();
                await process.StandardInput.WriteAsync(script);
                process.StandardInput.Close();
                var error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
                Assert.AreEqual(0, process.ExitCode, error);
            }
        }
    }

    [TestMethod]
    public void InvalidPeerAndDuplicateSlot_AreRejectedBeforeExecutingCommands()
    {
        var id = Guid.NewGuid();
        foreach (var address in new[] { "203.0.113.1; touch /tmp/unsafe", "::1", "127.0.0.1", "" })
            Assert.ThrowsExactly<InvalidOperationException>(() =>
                EgressNetworkManager.BuildScript(new(false, [new(id, 1, false, address)])));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EgressNetworkManager.BuildScript(new(false, [new(id, 0, false, "203.0.113.1")])));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            EgressNetworkManager.BuildScript(new(false, [new(id, 1, false, "203.0.113.1"), new(Guid.NewGuid(), 1, false, "203.0.113.2")])));
    }

    [TestMethod]
    public void TunnelAddresses_DoNotOverlapAndAvoidReservedLinkLocalRanges()
    {
        var addresses = new HashSet<string>();
        for (var slot = 1; slot <= 16000; slot++)
        {
            Assert.IsTrue(addresses.Add(EgressAddressing.Source(slot)));
            Assert.IsTrue(addresses.Add(EgressAddressing.Exit(slot)));
        }
        Assert.AreEqual("169.254.1.1", EgressAddressing.Source(1));
        Assert.IsFalse(addresses.Any(a => a.StartsWith("169.254.0.") || a.StartsWith("169.254.255.")));
    }
}
