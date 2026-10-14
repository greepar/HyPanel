using System.ComponentModel;
using HyPanel.Agent;
using HyPanel.Agent.Networking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Networking;

[TestClass]
public sealed class FirewallManagerTests
{
    private sealed class FakeHost
    {
        public readonly List<string> Commands = [];
        public bool Firewalld = true;
        public bool HasIptables = true;
        public string? Denied;
        public string ActiveZones = "public\n  interfaces: eth0\n";
        public string PermanentZones = "block dmz drop public trusted";
        public string Policies = "allow-host-ipv6";
        private bool chainJumped;

        public Task<FirewallCommandResult> Run(string file, IReadOnlyList<string> args, CancellationToken _)
        {
            Commands.Add(file + " " + string.Join(' ', args));
            if (file == "iptables")
            {
                if (!HasIptables) throw new Win32Exception();
                if (args.Contains("-C")) return Task.FromResult(new FirewallCommandResult(chainJumped ? 0 : 1, ""));
                if (args.Contains("-I")) chainJumped = true;
                if (args.Contains("-D")) { var was = chainJumped; chainJumped = false; return Task.FromResult(new FirewallCommandResult(was ? 0 : 1, "")); }
                return Task.FromResult(new FirewallCommandResult(0, ""));
            }
            if (!Firewalld) throw new Win32Exception();
            if (Denied is not null) return Task.FromResult(new FirewallCommandResult(1, Denied));
            if (args.Contains("--state")) return Task.FromResult(new FirewallCommandResult(0, "running\n"));
            if (args.Contains("--get-active-zones")) return Task.FromResult(new FirewallCommandResult(0, ActiveZones));
            if (args.Contains("--get-default-zone")) return Task.FromResult(new FirewallCommandResult(0, "public\n"));
            if (args.Contains("--get-zones")) return Task.FromResult(new FirewallCommandResult(0, PermanentZones));
            if (args.Contains("--get-policies")) return Task.FromResult(new FirewallCommandResult(0, Policies));
            foreach (var arg in args)
            {
                if (arg.StartsWith("--new-zone=", StringComparison.Ordinal)) PermanentZones += " " + arg[11..];
                if (arg.StartsWith("--new-policy=", StringComparison.Ordinal)) Policies += " " + arg[13..];
            }
            return Task.FromResult(new FirewallCommandResult(0, "success"));
        }
    }

    private static (FirewallManager Manager, FakeHost Host) Create(string? directory = null)
    {
        var host = new FakeHost();
        directory ??= Directory.CreateTempSubdirectory("hypanel-fw").FullName;
        return (new FirewallManager(NullLogger<FirewallManager>.Instance,
            new AgentEnrollmentOptions(null, null, directory), host.Run), host);
    }

    [TestMethod]
    public async Task Firewalld_OpensSourceRestrictedUdpPortInRuntimeAndPermanentConfig()
    {
        var (manager, host) = Create();
        var error = await manager.ApplyAsync("egress", [new("udp", 47541, "203.0.113.7")], false, CancellationToken.None);
        Assert.IsNull(error);
        const string rule = "rule family=\"ipv4\" source address=\"203.0.113.7\" port port=\"47541\" protocol=\"udp\" accept";
        CollectionAssert.Contains(host.Commands, $"firewall-cmd --zone=public --add-rich-rule={rule}");
        CollectionAssert.Contains(host.Commands, $"firewall-cmd --permanent --zone=public --add-rich-rule={rule}");
        Assert.IsFalse(host.Commands.Any(c => c.Contains("--reload")), "plain openings must not reload and drop runtime rules");
        Assert.IsFalse(host.Commands.Any(c => c.StartsWith("iptables")));
    }

    [TestMethod]
    public async Task Firewalld_PlainTcpPortAndGreProtocolUseNativeForms()
    {
        var (manager, host) = Create();
        await manager.ApplyAsync("relay", [new("tcp", 21114, null)], false, CancellationToken.None);
        await manager.ApplyAsync("egress", [new("gre", null, "203.0.113.7")], false, CancellationToken.None);
        CollectionAssert.Contains(host.Commands, "firewall-cmd --zone=public --add-port=21114/tcp");
        CollectionAssert.Contains(host.Commands,
            "firewall-cmd --permanent --zone=public --add-rich-rule=rule family=\"ipv4\" source address=\"203.0.113.7\" protocol value=\"gre\" accept");
    }

    [TestMethod]
    public async Task Firewalld_RemovesOpeningsWhenAnOwnerStopsNeedingThem()
    {
        var (manager, host) = Create();
        await manager.ApplyAsync("relay", [new("tcp", 21114, null)], false, CancellationToken.None);
        await manager.ApplyAsync("relay", [], false, CancellationToken.None);
        CollectionAssert.Contains(host.Commands, "firewall-cmd --zone=public --remove-port=21114/tcp");
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --zone=public --remove-port=21114/tcp");
    }

    [TestMethod]
    public async Task Firewalld_KeepsAnOpeningWhileAnotherOwnerStillNeedsIt()
    {
        var (manager, host) = Create();
        var shared = new FirewallOpening("udp", 51820, "203.0.113.7");
        await manager.ApplyAsync("a", [shared], false, CancellationToken.None);
        await manager.ApplyAsync("b", [shared], false, CancellationToken.None);
        await manager.ApplyAsync("a", [], false, CancellationToken.None);
        Assert.IsFalse(host.Commands.Any(c => c.Contains("--remove-rich-rule")));
    }

    [TestMethod]
    public async Task Firewalld_CreatesTunnelZoneAndForwardingPolicyOnceThenRemovesThem()
    {
        var (manager, host) = Create();
        Assert.IsNull(await manager.ApplyAsync("egress", [], true, CancellationToken.None));
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --new-zone=hypanel-tunnel");
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --zone=hypanel-tunnel --add-source=169.254.0.0/16");
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --new-policy=hypanel-egress");
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --policy=hypanel-egress --add-ingress-zone=hypanel-tunnel");
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --policy=hypanel-egress --add-egress-zone=ANY");
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --policy=hypanel-egress --set-target=ACCEPT");
        Assert.AreEqual(1, host.Commands.Count(c => c == "firewall-cmd --reload"));
        // The tunnel zone must stay out of trusted: the peer may forward through the host but not reach its services.
        Assert.IsFalse(host.Commands.Any(c => c.Contains("--zone=trusted")));

        host.Commands.Clear();
        await manager.ApplyAsync("egress", [], false, CancellationToken.None);
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --delete-policy=hypanel-egress");
        CollectionAssert.Contains(host.Commands, "firewall-cmd --permanent --delete-zone=hypanel-tunnel");
    }

    [TestMethod]
    public async Task Firewalld_DoesNotRecreateExistingTunnelZoneOrReload()
    {
        var (manager, host) = Create();
        host.PermanentZones += " hypanel-tunnel";
        host.Policies += " hypanel-egress";
        await manager.ApplyAsync("egress", [], true, CancellationToken.None);
        Assert.IsFalse(host.Commands.Any(c => c.Contains("--new-") || c.Contains("--reload")));
    }

    [TestMethod]
    public async Task Firewalld_PicksTheZoneThatOwnsAnInterfaceInsteadOfTheDefaultZone()
    {
        var (manager, host) = Create();
        host.ActiveZones = "hypanel-tunnel\n  sources: 169.254.0.0/16\nFedoraServer\n  interfaces: eth0\n";
        await manager.ApplyAsync("relay", [new("tcp", 9, null)], false, CancellationToken.None);
        CollectionAssert.Contains(host.Commands, "firewall-cmd --zone=FedoraServer --add-port=9/tcp");
    }

    [TestMethod]
    public async Task Firewalld_StripsTheDefaultMarkerFromTheZoneName()
    {
        var (manager, host) = Create();
        host.ActiveZones = "public (default)\n  interfaces: eth0\n";
        await manager.ApplyAsync("relay", [new("tcp", 9, null)], false, CancellationToken.None);
        CollectionAssert.Contains(host.Commands, "firewall-cmd --zone=public --add-port=9/tcp");
    }

    [TestMethod]
    public async Task Firewalld_FallsBackToTheDefaultZoneWhenNoInterfaceIsAssigned()
    {
        var (manager, host) = Create();
        host.ActiveZones = "hypanel-tunnel\n  sources: 169.254.0.0/16\npublic (default)\n";
        await manager.ApplyAsync("relay", [new("tcp", 9, null)], false, CancellationToken.None);
        CollectionAssert.Contains(host.Commands, "firewall-cmd --zone=public --add-port=9/tcp");
    }

    [TestMethod]
    public async Task Firewalld_UnauthorizedAgentGetsAnActionableMessageNotARawError()
    {
        var (manager, host) = Create();
        host.Denied = "Authorization failed.\n    Make sure polkit agent is running or run the application as superuser.";
        var error = await manager.ApplyAsync("relay", [new("tcp", 21114, null)], false, CancellationToken.None);
        StringAssert.Contains(error, "polkit");
        Assert.AreEqual(error, manager.LastError);
    }

    [TestMethod]
    public async Task Iptables_UsesADedicatedInputChainWhenFirewalldIsAbsent()
    {
        var (manager, host) = Create();
        host.Firewalld = false;
        Assert.IsNull(await manager.ApplyAsync("egress", [new("udp", 47541, "203.0.113.7"), new("gre", null, "203.0.113.7")], false,
            CancellationToken.None));
        CollectionAssert.Contains(host.Commands, "iptables -w -A HYPANEL-INPUT -s 203.0.113.7 -p udp --dport 47541 -j ACCEPT");
        CollectionAssert.Contains(host.Commands, "iptables -w -A HYPANEL-INPUT -s 203.0.113.7 -p gre -j ACCEPT");
        CollectionAssert.Contains(host.Commands, "iptables -w -I INPUT 1 -j HYPANEL-INPUT");

        host.Commands.Clear();
        await manager.ApplyAsync("egress", [], false, CancellationToken.None);
        CollectionAssert.Contains(host.Commands, "iptables -w -X HYPANEL-INPUT");
    }

    [TestMethod]
    public async Task NoFirewallTools_ReportsAnErrorOnlyWhenSomethingNeedsOpening()
    {
        var (manager, host) = Create();
        host.Firewalld = false;
        host.HasIptables = false;
        Assert.IsNull(await manager.ApplyAsync("relay", [], false, CancellationToken.None));
        Assert.IsNotNull(await manager.ApplyAsync("relay", [new("tcp", 21114, null)], false, CancellationToken.None));
    }

    [TestMethod]
    public async Task UnchangedDesiredStateIsNotReappliedOnEverySync()
    {
        var (manager, host) = Create();
        var openings = new FirewallOpening[] { new("tcp", 21114, null) };
        await manager.ApplyAsync("relay", openings, false, CancellationToken.None);
        host.Commands.Clear();
        await manager.ApplyAsync("relay", openings, false, CancellationToken.None);
        Assert.AreEqual(0, host.Commands.Count);
    }

    [TestMethod]
    public async Task NothingDesiredAndNothingAppliedRunsNoCommandsAtAll()
    {
        var (manager, host) = Create();
        await manager.ApplyAsync("egress", [], false, CancellationToken.None);
        Assert.AreEqual(0, host.Commands.Count);
    }

    [TestMethod]
    public async Task RulesLeftByAPreviousRunAreRemovedAfterRestart()
    {
        var directory = Directory.CreateTempSubdirectory("hypanel-fw").FullName;
        var (first, _) = Create(directory);
        await first.ApplyAsync("relay", [new("tcp", 21114, null)], false, CancellationToken.None);

        var (second, host) = Create(directory); // A new process: only the state file remembers the rule.
        await second.ApplyAsync("relay", [], false, CancellationToken.None);
        CollectionAssert.Contains(host.Commands, "firewall-cmd --zone=public --remove-port=21114/tcp");
    }
}
