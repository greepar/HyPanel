namespace HyPanel.Agent;

using System.Diagnostics;
using System.Net;
using System.Text;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Networking;
using HyPanel.Shared.Contracts;

/// <summary>Fixed, read-only network probes. Never runs arbitrary commands or exports WireGuard secrets.</summary>
public sealed class EgressDiagnosticCollector(EgressNetworkManager network, AgentEnrollmentOptions options)
{
    public async Task<string?> CollectAsync(Guid serviceId, CancellationToken ct)
    {
        var state = network.DesiredNetwork;
        var tunnels = state?.Tunnels.Where(t => t.ServiceId == serviceId).ToArray();
        if (tunnels is not { Length: > 0 }) return null;
        var output = new StringBuilder($"=== 出口网络诊断：{Environment.MachineName} ===\n时间：{DateTimeOffset.UtcNow:O}\n");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            foreach (var tunnel in tunnels)
            {
                var name = EgressTransportRegistry.Default.Resolve(tunnel.Transport).Interface(tunnel);
                var local = tunnel.IsExit ? EgressAddressing.Exit(tunnel.Slot) : EgressAddressing.Source(tunnel.Slot);
                var peer = tunnel.IsExit ? EgressAddressing.Source(tunnel.Slot) : EgressAddressing.Exit(tunnel.Slot);
                output.AppendLine($"角色：{(tunnel.IsExit ? "出口" : "入口")} 后端：{tunnel.Transport} 接口：{name} 对端公网：{tunnel.RemoteIpv4}");
                output.AppendLine($"$ interface {name}");
                output.AppendLine(KernelSettings.DescribeInterface(name));
                await Probe("ip", ["-4", "route", "get", peer, "from", local]);
                if (!tunnel.IsExit) await Probe("ip", ["-4", "route", "show", "table", EgressAddressing.Table(tunnel.Slot).ToString()]);
                if (tunnel.Transport == EgressTransports.WireGuard && state!.WireGuardTool is { } tool)
                {
                    var wg = Path.Combine(options.DataDirectory, "tools", tool.BackendType, tool.Version, tool.Rid, tool.FileName);
                    foreach (var field in new[] { "public-key", "listen-port", "peers", "endpoints", "allowed-ips", "latest-handshakes", "transfer" })
                        await Probe(wg, ["show", name, field]);
                }
                output.AppendLine($"$ icmp {local} -> {peer}");
                output.AppendLine(await IcmpProbe.DescribeAsync(IPAddress.Parse(local), IPAddress.Parse(peer), 2, TimeSpan.FromSeconds(2), deadline.Token));
            }
            await Probe("ip", ["-4", "rule", "show"]);
            await Probe("nft", ["list", "ruleset"]);
            await Probe("iptables", ["-S", "INPUT"]);
            await Probe("iptables", ["-S", "FORWARD"]);
            output.AppendLine("$ /proc/sys");
            output.AppendLine(KernelSettings.Describe("net/ipv4/ip_forward", "net/ipv4/icmp_echo_ignore_all", "net/ipv4/conf/all/rp_filter"));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { output.AppendLine("诊断达到时间限制，以上为已收集结果。"); }
        return ServiceLogCollector.TailUtf8(output.ToString(), 30000);

        async Task Probe(string executable, string[] arguments)
        {
            output.AppendLine($"$ {Path.GetFileName(executable)} {string.Join(' ', arguments)}");
            output.AppendLine(await RunAsync(executable, arguments, deadline.Token));
        }
    }

    internal static async Task<string> RunAsync(string executable, string[] arguments, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = new(executable) {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        process.StartInfo.Environment["PATH"] = "/usr/sbin:/usr/bin:/sbin:/bin";
        process.StartInfo.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            process.Start();
            var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return $"退出码：{process.ExitCode}\n{await stdout}{await stderr}";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return "命令超时。"; }
        catch (System.ComponentModel.Win32Exception) { return "命令不存在或无执行权限。"; }
        finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        var truncated = false;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            var take = Math.Min(count, Math.Max(0, 8192 - result.Length));
            result.Append(buffer, 0, take);
            truncated |= take < count;
        }
        return result + (truncated ? "\n[输出已截断]\n" : "");
    }
}
