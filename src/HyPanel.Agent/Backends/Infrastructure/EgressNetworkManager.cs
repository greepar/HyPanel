namespace HyPanel.Agent.Backends.Infrastructure;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HyPanel.Agent.Networking;
using HyPanel.Shared.Contracts;

/// <summary>Owns hpe* exit interfaces, routing tables 100001..116000 and inet hypanel_egress.</summary>
public sealed class EgressNetworkManager(ILogger<EgressNetworkManager> logger, EgressTransportRegistry? transports = null)
{
    private EgressNetworkReport report = new(false, false, null);
    public EgressNetworkReport Report
    {
        get => report with { SupportedTransports = Transports.SupportedTransports };
        private set => report = value;
    }
    public IReadOnlyDictionary<Guid, string> Failures { get; private set; } = new Dictionary<Guid, string>();
    private bool hasManagedNetwork;
    private EgressTransportRegistry Transports => transports ?? EgressTransportRegistry.Default;

    public async Task ApplyAsync(EgressNetworkState? state, CancellationToken ct)
    {
        state ??= new(false, []);
        var failures = new Dictionary<Guid, string>();
        if (!state.Enabled && state.Tunnels.Count == 0 && !hasManagedNetwork)
        {
            Failures = failures;
            Report = new(false, false, null, Transports.SupportedTransports, state.Transport);
            return;
        }
        try
        {
            if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("出口转发仅支持 Linux 节点。");
            var script = BuildScriptCore(state, Transports);
            hasManagedNetwork = true;
            await RunAsync(script, ct);
            Report = new(state.Enabled, state.Enabled, null, Transports.SupportedTransports, state.Transport);
            foreach (var tunnel in state.Tunnels.Where(t => !t.IsExit))
            {
                try
                {
                    await RunAsync($"ping -n -c 1 -W 1 -I {EgressAddressing.Source(tunnel.Slot)} {EgressAddressing.Exit(tunnel.Slot)} >/dev/null\n", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures[tunnel.ServiceId] = "出口隧道不可达，请检查两端公网 IPv4、所选后端的协议和防火墙。";
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = ex.Message.Length > 1024 ? ex.Message[..1024] : ex.Message;
            if (Report.Error != error) logger.LogWarning("Egress network failed: {Error}", error);
            Report = new(state.Enabled, false, error, Transports.SupportedTransports, state.Transport);
            foreach (var tunnel in state.Tunnels.Where(t => !t.IsExit)) failures[tunnel.ServiceId] = error;
        }
        Failures = failures;
    }

    internal static string BuildScript(EgressNetworkState state) => BuildScriptCore(state, EgressTransportRegistry.Default);

    internal static string BuildScriptCore(EgressNetworkState state, EgressTransportRegistry registry)
    {
        if (state.Tunnels.Count > 16000 || state.Tunnels.Select(t => t.Slot).Distinct().Count() != state.Tunnels.Count)
            throw new InvalidOperationException("Invalid exit tunnel allocation.");
        registry.Resolve(state.Transport);
        foreach (var t in state.Tunnels)
            if (!EgressAddressing.IsValidSlot(t.Slot) || t.ServiceId == Guid.Empty
                || !IPAddress.TryParse(t.RemoteIpv4, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork
                || ip.ToString() != t.RemoteIpv4 || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any)
                || t.IsExit && !state.Enabled)
                throw new InvalidOperationException("隧道对端公网 IPv4 或出口配置无效。");
        var script = new StringBuilder("set -eu\nexport PATH=/usr/sbin:/usr/bin:/sbin:/bin\n");
        var active = state.Enabled || state.Tunnels.Count > 0;
        if (active)
        {
            script.AppendLine("command -v ip >/dev/null && command -v nft >/dev/null && command -v ping >/dev/null || { echo '缺少 iproute2、nftables 或 ping，请用新版节点安装命令安装网络工具。' >&2; exit 1; }");
            // Source-address routing requires loose reverse-path checks on encapsulated return traffic.
            script.AppendLine("if [ \"$(cat /proc/sys/net/ipv4/conf/all/rp_filter)\" = 1 ]; then sysctl -q -w net.ipv4.conf.all.rp_filter=2; fi");
        }
        if (state.Enabled)
        {
            script.AppendLine("sysctl -q -w net.ipv4.ip_forward=1");
            script.AppendLine("sysctl -q -w net.ipv4.conf.all.rp_filter=2");
            // Check the selected backend before advertising this node as an exit.
            script.Append(registry.Resolve(state.Transport).PrepareExit(state.TransportOptionsJson));
        }
        foreach (var t in state.Tunnels.OrderBy(t => t.Slot))
        {
            var name = EgressAddressing.Interface(t.Slot);
            var local = t.IsExit ? EgressAddressing.Exit(t.Slot) : EgressAddressing.Source(t.Slot);
            var peer = t.IsExit ? EgressAddressing.Source(t.Slot) : EgressAddressing.Exit(t.Slot);
            var table = EgressAddressing.Table(t.Slot);
            var pref = EgressAddressing.Priority(t.Slot);
            var backend = registry.Resolve(t.Transport);
            script.Append(backend.ConfigureTunnel(t));
            script.AppendLine($"ip address replace {local}/30 dev {name}");
            script.AppendLine($"ip link set {name} mtu {backend.Definition.Mtu} up");
            script.AppendLine($"sysctl -q -w net.ipv4.conf.{name}.rp_filter=2");
            if (!t.IsExit)
            {
                // The unreachable route survives interface deletion and stops lookup falling through to main.
                script.AppendLine($"ip route replace unreachable default metric 32760 table {table}");
                script.AppendLine($"ip -4 rule show | grep -Fq 'from {local} lookup {table}' || ip rule add pref {pref} from {local}/32 table {table}");
                script.AppendLine($"ip route replace {peer}/32 dev {name} src {local} table {table}");
                script.AppendLine($"ip route replace default via {peer} dev {name} src {local} metric 10 table {table}");
            }
        }
        // Atomically replace our rules. Other firewall tables are never flushed.
        script.AppendLine("if command -v nft >/dev/null; then nft -f - <<'HYPANEL_NFT'");
        script.Append(BuildNftCore(state, registry));
        script.AppendLine("HYPANEL_NFT\nfi");
        var keep = string.Join("|", state.Tunnels.Select(t => EgressAddressing.Interface(t.Slot)));
        script.AppendLine("for path in /sys/class/net/hpe*; do");
        script.AppendLine("  [ -e \"$path\" ] || continue; name=${path##*/}; slot=${name#hpe}");
        script.AppendLine("  case \"$slot\" in ''|*[!0-9]*) continue;; esac");
        script.AppendLine("  [ \"$slot\" -ge 1 ] && [ \"$slot\" -le 16000 ] || continue");
        if (keep.Length > 0) script.AppendLine($"  case \"$name\" in {keep}) continue;; esac");
        script.AppendLine("  ip link delete \"$name\"\n  ip rule del pref \"$((10000 + slot))\" 2>/dev/null || true\n  ip route flush table \"$((100000 + slot))\"\ndone");
        // Also remove orphan source rules if an interface was deleted outside the Agent.
        script.AppendLine("if command -v ip >/dev/null; then");
        script.AppendLine("for slot in $(ip -4 rule show | awk '$1 ~ /^[0-9]+:$/ {p=$1+0; if(p>10000 && p<=26000) for(i=1;i<=NF;i++) if($i==\"lookup\" && $(i+1)==p+90000) print p-10000}'); do");
        if (keep.Length > 0) script.AppendLine($"  case \"hpe$slot\" in {keep}) continue;; esac");
        script.AppendLine("  ip rule del pref \"$((10000 + slot))\" 2>/dev/null || true\n  ip route flush table \"$((100000 + slot))\"\ndone\nfi");
        return script.ToString();
    }

    internal static string BuildNft(EgressNetworkState state) => BuildNftCore(state, EgressTransportRegistry.Default);

    private static string BuildNftCore(EgressNetworkState state, EgressTransportRegistry registry)
    {
        var s = new StringBuilder("add table inet hypanel_egress\ndelete table inet hypanel_egress\n");
        if (!state.Enabled && state.Tunnels.Count == 0) return s.ToString();
        s.AppendLine("table inet hypanel_egress {");
        s.AppendLine(" chain input { type filter hook input priority -10; policy accept;");
        foreach (var t in state.Tunnels)
        {
            var name = EgressAddressing.Interface(t.Slot);
            var peer = t.IsExit ? EgressAddressing.Source(t.Slot) : EgressAddressing.Exit(t.Slot);
            s.Append("  ").AppendLine(registry.Resolve(t.Transport).InputFirewallRule(t));
            // Only the assigned peer may inject payloads through this tunnel.
            if (t.IsExit) s.AppendLine($"  iifname \"{name}\" ip saddr != {peer} drop");
            s.AppendLine($"  iifname \"{name}\" ip protocol icmp accept");
        }
        s.AppendLine(" }");
        // Enforce the requested exit even if another administrator's earlier policy rule catches this source.
        s.AppendLine(" chain output { type filter hook output priority 0; policy accept;");
        foreach (var t in state.Tunnels.Where(t => !t.IsExit))
            s.AppendLine($"  ip saddr {EgressAddressing.Source(t.Slot)} oifname != \"{EgressAddressing.Interface(t.Slot)}\" drop");
        s.AppendLine(" }");
        s.AppendLine(" chain forward { type filter hook forward priority -10; policy accept;");
        foreach (var t in state.Tunnels.Where(t => t.IsExit))
        {
            var name = EgressAddressing.Interface(t.Slot);
            var source = EgressAddressing.Source(t.Slot);
            var mss = registry.Resolve(t.Transport).Definition.Mtu - 40;
            s.AppendLine($"  iifname \"{name}\" ip saddr != {source} drop");
            s.AppendLine($"  iifname \"{name}\" tcp flags & syn == syn tcp option maxseg size > {mss} tcp option maxseg size set {mss}");
            s.AppendLine($"  oifname \"{name}\" tcp flags & syn == syn tcp option maxseg size > {mss} tcp option maxseg size set {mss}");
            s.AppendLine($"  iifname \"{name}\" ip saddr {source} accept");
            s.AppendLine($"  oifname \"{name}\" ip daddr {source} ct state established,related accept");
            s.AppendLine($"  oifname \"{name}\" drop");
        }
        s.AppendLine(" }");
        s.AppendLine(" chain postrouting { type nat hook postrouting priority srcnat; policy accept;");
        foreach (var t in state.Tunnels.Where(t => t.IsExit))
            s.AppendLine($"  iifname \"{EgressAddressing.Interface(t.Slot)}\" ip saddr {EgressAddressing.Source(t.Slot)} masquerade");
        s.AppendLine(" }\n}");
        return s.ToString();
    }

    private static async Task RunAsync(string script, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = new Process { StartInfo = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        }};
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteAsync(script.AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await output;
            var detail = await error;
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
                ? "出口网络配置失败。" : detail.Trim());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("出口网络配置超时。");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
