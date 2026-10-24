namespace HyPanel.Agent.Networking;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

/// <summary>An inbound packet class that must reach the host: TCP/UDP to a port, or the GRE protocol.</summary>
public sealed record FirewallOpening(string Protocol, int? Port, string? Source)
{
    internal string Key => $"{Protocol}|{Port}|{Source}";
}

public sealed record FirewallCommandResult(int ExitCode, string Output);

public delegate Task<FirewallCommandResult> FirewallCommandRunner(string file, IReadOnlyList<string> arguments,
    CancellationToken cancellationToken);

/// <summary>
/// Opens the ports and protocols HyPanel's forwarding features need, so administrators do not have to edit their
/// firewall by hand. Each feature owns a set of openings; the union is reconciled against what this Agent added
/// earlier, so rules disappear when a feature stops needing them.
/// <list type="bullet">
/// <item>firewalld (nftables backend): rules go to the firewalld daemon with <c>firewall-cmd</c>, which needs
/// root or a polkit rule for the Agent user (written by the installer). An nft accept in our own table cannot
/// override firewalld's reject, so this is the only way through.</item>
/// <item>Other hosts with iptables (ufw, Docker, plain iptables): a dedicated <c>HYPANEL-INPUT</c> chain jumped to
/// first from INPUT, mirrored in ip6tables for openings that are not limited to an IPv4 source. Needs only
/// CAP_NET_ADMIN.</item>
/// </list>
/// Earlier versions also created a tunnel zone and forwarding policy for exit forwarding; those are removed when found.
/// </summary>
public sealed class FirewallManager(ILogger<FirewallManager> logger, AgentEnrollmentOptions? options = null,
    FirewallCommandRunner? runner = null, TimeProvider? time = null)
{
    internal const string InputChain = "HYPANEL-INPUT";
    internal const string TunnelZone = "hypanel-tunnel";
    internal const string TunnelPolicy = "hypanel-egress";
    private static readonly TimeSpan VerifyInterval = TimeSpan.FromMinutes(5);
    /// <summary>A failing reconcile is retried at this pace instead of on every sync, and is logged once per message.</summary>
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, HashSet<FirewallOpening>> owners = new();
    private readonly FirewallCommandRunner run = runner ?? RunProcessAsync;
    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private State? applied;
    private string? signature;
    private DateTimeOffset verifiedAt;
    private string? failedSignature;
    private string? failedError;
    private DateTimeOffset failedAt;

    /// <summary>Why the last reconcile failed; null when firewall rules are in place or none are needed.</summary>
    public string? LastError { get; private set; }

    private string StatePath => Path.Combine(options?.DataDirectory ?? Path.GetTempPath(), "firewall-rules.txt");

    /// <summary>Declares what <paramref name="owner"/> needs and reconciles the firewall. Returns the error, if any.</summary>
    public async Task<string?> ApplyAsync(string owner, IReadOnlyCollection<FirewallOpening> openings,
        CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (openings.Count == 0) owners.Remove(owner);
            else owners[owner] = openings.ToHashSet();
            LastError = await ReconcileAsync(ct);
            return LastError;
        }
        finally { gate.Release(); }
    }

    private async Task<string?> ReconcileAsync(CancellationToken ct)
    {
        var desired = new HashSet<FirewallOpening>(owners.Values.SelectMany(o => o));
        applied ??= Load();
        var wanted = Signature(desired);
        if (applied.Openings.Count == 0 && !applied.Forwarding && desired.Count == 0)
        {
            signature = wanted;
            return null;
        }
        if (wanted == signature && clock.GetUtcNow() - verifiedAt < VerifyInterval) return null;
        if (wanted == failedSignature && failedError is not null && clock.GetUtcNow() - failedAt < RetryInterval)
            return failedError;

        // An injected runner (tests) stands in for the Linux tools.
        if (!OperatingSystem.IsLinux() && runner is null)
            return desired.Count == 0 && !applied.Forwarding ? null : "防火墙自动放行仅支持 Linux 节点。";
        try
        {
            var error = await DetectFirewalldAsync(ct)
                ? await ReconcileFirewalldAsync(desired, ct)
                : await ReconcileIptablesAsync(desired, ct);
            if (error is null)
            {
                signature = wanted;
                verifiedAt = clock.GetUtcNow();
                failedSignature = failedError = null;
                applied = applied with { Openings = desired.Select(o => o.Key).ToHashSet(), Forwarding = false };
                Save(applied);
                return null;
            }
            return Fail(wanted, error);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return Fail(wanted, exception.Message);
        }
    }

    private string Fail(string wanted, string error)
    {
        if (error != failedError) logger.LogWarning("Could not update firewall rules: {Error}", error);
        failedSignature = wanted;
        failedError = error;
        failedAt = clock.GetUtcNow();
        return error;
    }

    private static string Signature(IEnumerable<FirewallOpening> desired) =>
        string.Join(';', desired.Select(o => o.Key).Order(StringComparer.Ordinal));

    // ---- firewalld ----------------------------------------------------------------------------------------

    private async Task<bool> DetectFirewalldAsync(CancellationToken ct)
    {
        try
        {
            var result = await run("firewall-cmd", ["--state"], ct);
            // Exit 0 and "running" is a live daemon; an authorization failure still proves one is installed.
            return result.ExitCode == 0 ? result.Output.Contains("running", StringComparison.Ordinal)
                : result.Output.Contains("Authorization failed", StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception) { return false; }
    }

    private async Task<string?> ReconcileFirewalldAsync(HashSet<FirewallOpening> desired, CancellationToken ct)
    {
        var zone = await ResolveZoneAsync(ct);
        if (zone is null) return Unauthorized();
        var current = applied!;
        if (current.Zone is not null && current.Zone != zone) // The interface moved zones: clean the old one.
            foreach (var key in current.Openings)
                if (Parse(key) is { } stale && await FirewalldRuleAsync(stale, current.Zone, remove: true, ct) is { } failure)
                    return failure;
        // Clean up the zone and policy an earlier version created for exit forwarding.
        if (current.Forwarding && await RemoveForwardingAsync(ct) is { } removeError) return removeError;

        foreach (var key in current.Openings)
            if (!desired.Any(o => o.Key == key) && Parse(key) is { } gone &&
                await FirewalldRuleAsync(gone, zone, remove: true, ct) is { } failure)
                return failure;
        foreach (var opening in desired)
            if (await FirewalldRuleAsync(opening, zone, remove: false, ct) is { } failure) return failure;
        applied = current with { Zone = zone };
        return null;
    }

    private string Unauthorized() =>
        "Agent 无权管理 firewalld：请在节点上重新运行一次安装命令（会写入 polkit 规则），或手动放行所需端口。";

    private async Task<string?> ResolveZoneAsync(CancellationToken ct)
    {
        var active = await run("firewall-cmd", ["--get-active-zones"], ct);
        if (active.ExitCode != 0) return IsDenied(active.Output) ? null : throw new InvalidOperationException(Trim(active.Output));
        string? zone = null;
        foreach (var raw in active.Output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            // The default zone is printed as "public (default)".
            if (!char.IsWhiteSpace(line[0])) { zone = line.Trim().Replace(" (default)", "", StringComparison.Ordinal); continue; }
            if (zone is not null && zone != TunnelZone && line.TrimStart().StartsWith("interfaces:", StringComparison.Ordinal)
                && line.TrimStart()["interfaces:".Length..].Trim().Length > 0) return zone;
        }
        var fallback = await run("firewall-cmd", ["--get-default-zone"], ct);
        if (fallback.ExitCode != 0) return IsDenied(fallback.Output) ? null : throw new InvalidOperationException(Trim(fallback.Output));
        return fallback.Output.Trim();
    }

    private static bool IsDenied(string output) => output.Contains("Authorization failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds or removes one opening in both the runtime and the permanent configuration.</summary>
    private async Task<string?> FirewalldRuleAsync(FirewallOpening opening, string zone, bool remove, CancellationToken ct)
    {
        var verb = remove ? "remove" : "add";
        string[] spec = opening.Source is null
            ? opening.Protocol == "gre" ? [$"--{verb}-protocol=gre"] : [$"--{verb}-port={opening.Port}/{opening.Protocol}"]
            : [$"--{verb}-rich-rule={RichRule(opening)}"];
        foreach (var permanent in new[] { false, true })
        {
            var arguments = new List<string>();
            if (permanent) arguments.Add("--permanent");
            arguments.Add($"--zone={zone}");
            arguments.AddRange(spec);
            var result = await run("firewall-cmd", arguments, ct);
            if (!Succeeded(result)) return IsDenied(result.Output) ? Unauthorized() : $"firewalld 规则设置失败：{Trim(result.Output)}";
        }
        return null;
    }

    internal static string RichRule(FirewallOpening opening) => opening.Protocol == "gre"
        ? $"rule family=\"ipv4\" source address=\"{opening.Source}\" protocol value=\"gre\" accept"
        : $"rule family=\"ipv4\" source address=\"{opening.Source}\" port port=\"{opening.Port}\" protocol=\"{opening.Protocol}\" accept";

    private async Task<string?> RemoveForwardingAsync(CancellationToken ct)
    {
        foreach (var command in new[]
                 {
                     new[] { "--permanent", $"--delete-policy={TunnelPolicy}" },
                     ["--permanent", $"--delete-zone={TunnelZone}"]
                 })
        {
            var result = await run("firewall-cmd", command, ct);
            var absent = result.Output.Contains("INVALID_ZONE", StringComparison.Ordinal) ||
                         result.Output.Contains("INVALID_POLICY", StringComparison.Ordinal);
            if (!Succeeded(result) && !absent) return IsDenied(result.Output) ? Unauthorized() : $"firewalld 转发策略清理失败：{Trim(result.Output)}";
        }
        var reload = await run("firewall-cmd", ["--reload"], ct);
        return Succeeded(reload) ? null : $"firewalld 重新加载失败：{Trim(reload.Output)}";
    }

    // firewall-cmd reports an already-present or already-absent item as a warning; both mean the goal is met.
    private static bool Succeeded(FirewallCommandResult result) => result.ExitCode == 0 ||
        result.Output.Contains("ALREADY_ENABLED", StringComparison.Ordinal) ||
        result.Output.Contains("NOT_ENABLED", StringComparison.Ordinal) ||
        result.Output.Contains("NAME_CONFLICT", StringComparison.Ordinal);

    // ---- iptables ------------------------------------------------------------------------------------------

    private async Task<string?> ReconcileIptablesAsync(HashSet<FirewallOpening> desired, CancellationToken ct)
    {
        try
        {
            var error = await ReconcileChainAsync("iptables", desired, ct);
            if (error is not null) return error;
        }
        catch (Win32Exception)
        {
            return desired.Count == 0 ? null : "节点没有 iptables 或 firewalld，无法自动放行端口；如有防火墙请手动放行所需端口。";
        }
        // IPv6 entries reach the same ports; source-restricted openings name IPv4 peers and stay IPv4 only.
        try { return await ReconcileChainAsync("ip6tables", desired.Where(o => o.Source is null).ToHashSet(), ct); }
        catch (Win32Exception) { return null; }
    }

    private async Task<string?> ReconcileChainAsync(string tool, HashSet<FirewallOpening> desired, CancellationToken ct)
    {
        if (desired.Count == 0)
        {
            // Best effort: the chain may never have existed.
            while ((await run(tool, ["-w", "-D", "INPUT", "-j", InputChain], ct)).ExitCode == 0) { }
            await run(tool, ["-w", "-F", InputChain], ct);
            await run(tool, ["-w", "-X", InputChain], ct);
            return null;
        }
        await run(tool, ["-w", "-N", InputChain], ct); // Fails harmlessly when it exists.
        var flush = await run(tool, ["-w", "-F", InputChain], ct);
        if (flush.ExitCode != 0) return $"无法写入 {tool} 放行规则：{Trim(flush.Output)}";
        foreach (var opening in desired)
        {
            var result = await run(tool, ["-w", "-A", InputChain, .. IptablesMatch(opening), "-j", "ACCEPT"], ct);
            if (result.ExitCode != 0) return $"无法写入 {tool} 放行规则：{Trim(result.Output)}";
        }
        if ((await run(tool, ["-w", "-C", "INPUT", "-j", InputChain], ct)).ExitCode != 0)
        {
            var jump = await run(tool, ["-w", "-I", "INPUT", "1", "-j", InputChain], ct);
            if (jump.ExitCode != 0) return $"无法写入 {tool} 放行规则：{Trim(jump.Output)}";
        }
        return null;
    }

    internal static string[] IptablesMatch(FirewallOpening opening)
    {
        var match = new List<string>();
        if (opening.Source is not null) match.AddRange(["-s", opening.Source]);
        match.AddRange(["-p", opening.Protocol]);
        if (opening.Port is { } port) match.AddRange(["--dport", port.ToString()]);
        return [.. match];
    }

    // ---- persistence ---------------------------------------------------------------------------------------

    private sealed record State(HashSet<string> Openings, bool Forwarding, string? Zone);

    private State Load()
    {
        try
        {
            if (!File.Exists(StatePath)) return new([], false, null);
            var openings = new HashSet<string>();
            var forwarding = false;
            string? zone = null;
            foreach (var line in File.ReadAllLines(StatePath))
                if (line == "forwarding") forwarding = true;
                else if (line.StartsWith("zone=", StringComparison.Ordinal)) zone = line[5..];
                else if (line.StartsWith("open=", StringComparison.Ordinal)) openings.Add(line[5..]);
            return new(openings, forwarding, zone);
        }
        catch (IOException) { return new([], false, null); }
    }

    private void Save(State state)
    {
        var text = new StringBuilder();
        if (state.Zone is not null) text.Append("zone=").Append(state.Zone).Append('\n');
        if (state.Forwarding) text.Append("forwarding\n");
        foreach (var key in state.Openings.Order(StringComparer.Ordinal)) text.Append("open=").Append(key).Append('\n');
        try { File.WriteAllText(StatePath, text.ToString()); }
        catch (IOException exception) { logger.LogDebug("Could not persist firewall state: {Error}", exception.Message); }
    }

    private static FirewallOpening? Parse(string key)
    {
        var parts = key.Split('|');
        return parts.Length == 3
            ? new FirewallOpening(parts[0], int.TryParse(parts[1], out var port) ? port : null, parts[2].Length == 0 ? null : parts[2])
            : null;
    }

    private static string Trim(string text) => text.Length > 400 ? text[..400].Trim() : text.Trim();

    private static async Task<FirewallCommandResult> RunProcessAsync(string file, IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new Win32Exception($"{file} could not be started");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new(process.ExitCode, await output + await error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return new(-1, $"{file} 执行超时。");
        }
    }
}
