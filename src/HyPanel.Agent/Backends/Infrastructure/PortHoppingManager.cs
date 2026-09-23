namespace HyPanel.Agent.Backends.Infrastructure;

using System.Diagnostics;
using System.Text;
using HyPanel.Agent.Backends.Hysteria2;
using HyPanel.Shared.Contracts;

/// <summary>
/// Hysteria2 port hopping: UDP traffic to the configured ports is redirected to the service's listen port with an
/// nftables table owned by HyPanel (<c>inet hypanel_hop</c>). The whole table is replaced atomically on every apply,
/// so removed services and disabled hopping leave no rules behind. Requires Linux, nft and CAP_NET_ADMIN.
/// </summary>
public sealed class PortHoppingManager(ILogger<PortHoppingManager> logger)
{
    public const string TableName = "hypanel_hop";
    private string? appliedScript;

    /// <summary>Last failure, reported on affected services; null when rules are in place or none are needed.</summary>
    public string? LastError { get; private set; }

    /// <summary>Service ids with port hopping in the most recently applied desired state.</summary>
    public IReadOnlySet<Guid> HoppingServices { get; private set; } = new HashSet<Guid>();

    public async Task ApplyAsync(NodeDesiredState? desired, CancellationToken cancellationToken)
    {
        var rules = new List<(Guid ServiceId, int ListenPort, string Ports)>();
        foreach (var service in desired?.Services ?? [])
            if (service.Enabled && service.BackendType == "hysteria2" &&
                Hysteria2Provider.PortHopping(service.ConfigJson) is { } hopping)
                rules.Add((service.ServiceId, hopping.ListenPort, hopping.Ports.ToString()));
        HoppingServices = rules.Select(rule => rule.ServiceId).ToHashSet();

        var script = BuildScript(rules.Select(rule => (rule.ListenPort, rule.Ports)).ToArray());
        if (script == appliedScript) return;
        if (!OperatingSystem.IsLinux())
        {
            LastError = rules.Count == 0 ? null : "端口跳跃仅支持 Linux 节点。";
            return;
        }
        var nft = new[] { "/usr/sbin/nft", "/sbin/nft", "/usr/bin/nft" }.FirstOrDefault(File.Exists);
        if (nft is null)
        {
            LastError = rules.Count == 0 ? null : "节点未安装 nftables（nft），无法启用端口跳跃。";
            return;
        }
        var (exitCode, error) = await RunAsync(nft, script, cancellationToken);
        if (exitCode == 0)
        {
            appliedScript = script;
            LastError = null;
            if (rules.Count > 0)
                logger.LogInformation("Port hopping active: {Rules}.",
                    string.Join("; ", rules.Select(rule => $"udp {rule.Ports} -> {rule.ListenPort}")));
            return;
        }
        LastError = rules.Count == 0
            ? null
            : error.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase)
                ? "Agent 缺少 CAP_NET_ADMIN 权限：请在节点上重新运行一次安装命令后再启用端口跳跃。"
                : $"端口跳跃规则设置失败：{error.Trim()}";
        logger.LogWarning("Could not apply port hopping rules (exit {ExitCode}): {Error}", exitCode, error.Trim());
    }

    /// <summary>nftables script that atomically replaces the HyPanel table.</summary>
    internal static string BuildScript(IReadOnlyList<(int ListenPort, string Ports)> rules)
    {
        var script = new StringBuilder();
        // "add" then "delete" makes the replacement idempotent whether or not the table exists yet.
        script.Append("add table inet ").Append(TableName).Append('\n');
        script.Append("delete table inet ").Append(TableName).Append('\n');
        if (rules.Count == 0) return script.ToString();
        script.Append("table inet ").Append(TableName).Append(" {\n");
        script.Append("  chain prerouting {\n");
        script.Append("    type nat hook prerouting priority dstnat; policy accept;\n");
        foreach (var (listenPort, ports) in rules)
            script.Append("    udp dport { ").Append(ports.Replace(",", ", ", StringComparison.Ordinal))
                .Append(" } redirect to :").Append(listenPort).Append('\n');
        script.Append("  }\n}\n");
        return script.ToString();
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(string nft, string script,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(nft)
        {
            RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("-f");
        start.ArgumentList.Add("-");
        try
        {
            using var process = Process.Start(start);
            if (process is null) return (-1, "nft could not be started");
            await process.StandardInput.WriteAsync(script);
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            _ = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await errorTask);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or OperationCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            return (-1, exception.Message);
        }
    }
}
