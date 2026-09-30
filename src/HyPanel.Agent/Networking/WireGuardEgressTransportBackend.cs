namespace HyPanel.Agent.Networking;

using System.Text;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

/// <summary>Kernel WireGuard transport. One exit interface and UDP listener serve every source peer.</summary>
public sealed class WireGuardEgressTransportBackend : IEgressTransportBackend
{
    public EgressTransportDefinition Definition => EgressTransports.WireGuardDefinition;

    public string PrepareExit(string? optionsJson)
    {
        var option = Parse(optionsJson);
        if (option.InterfaceName != "hpwx") throw new InvalidOperationException("WireGuard 出口接口无效。");
        return "command -v \"$HYPANEL_WG\" >/dev/null || { echo 'WireGuard 工具不可用，请更新到正式发布的 Linux Agent。' >&2; exit 1; }\n" +
            EnsureInterface("hpwx") + Config("hpwx", option.PrivateKey, option.Port, []) + $"ip link set hpwx mtu {Definition.Mtu} up\n";
    }

    public string PrepareTunnels(IReadOnlyList<EgressTunnel> tunnels)
    {
        var script = new StringBuilder("command -v \"$HYPANEL_WG\" >/dev/null || { echo 'WireGuard 工具不可用，请更新到正式发布的 Linux Agent。' >&2; exit 1; }\n");
        foreach (var group in tunnels.GroupBy(t => Interface(t)))
        {
            var first = group.First();
            var option = Parse(first.TransportOptionsJson);
            if (group.Any(t => Parse(t.TransportOptionsJson).PeerPublicKey is null))
                throw new InvalidOperationException("WireGuard 缺少对端公钥。");
            if (group.Any(t => t.IsExit != first.IsExit || Parse(t.TransportOptionsJson).PrivateKey != option.PrivateKey
                || Parse(t.TransportOptionsJson).Port != option.Port))
                throw new InvalidOperationException("WireGuard 共享接口配置冲突。");
            script.Append(EnsureInterface(group.Key));
            if (first.IsExit)
            {
                var peers = group.GroupBy(t => Parse(t.TransportOptionsJson).PeerPublicKey, StringComparer.Ordinal)
                    .Select(g => new Peer(g.Key!, string.Join(", ", g.Select(t => EgressAddressing.Source(t.Slot) + "/32")), null, null))
                    .ToArray();
                script.Append(Config(group.Key, option.PrivateKey, option.Port, peers));
            }
            else
            {
                if (group.Any(t => Parse(t.TransportOptionsJson).PeerPublicKey != option.PeerPublicKey
                    || t.RemoteIpv4 != first.RemoteIpv4))
                    throw new InvalidOperationException("WireGuard 出口对端配置冲突。");
                script.Append(Config(group.Key, option.PrivateKey, null,
                    [new Peer(option.PeerPublicKey!, "0.0.0.0/0", $"{first.RemoteIpv4}:{option.Port}", 25)]));
            }
        }
        return script.ToString();
    }

    public string Interface(EgressTunnel tunnel)
    {
        var name = Parse(tunnel.TransportOptionsJson).InterfaceName;
        if (tunnel.IsExit != (name == "hpwx")) throw new InvalidOperationException("WireGuard 接口角色无效。");
        return name;
    }
    public string ConfigureTunnel(EgressTunnel tunnel) => string.Empty;

    public string InputFirewallRule(EgressTunnel tunnel) => tunnel.IsExit
        ? $"ip saddr {tunnel.RemoteIpv4} ip protocol udp udp dport {Parse(tunnel.TransportOptionsJson).Port} accept"
        : string.Empty;

    private static string EnsureInterface(string name) =>
        $"ip link show {name} >/dev/null 2>&1 || ip link add {name} type wireguard || {{ echo '内核不支持 WireGuard 或 Agent 缺少 NET_ADMIN 权限。' >&2; exit 1; }}\n";

    private static string Config(string name, string privateKey, int? port, IReadOnlyList<Peer> peers)
    {
        var s = new StringBuilder($"\"$HYPANEL_WG\" syncconf {name} /dev/stdin <<'HYPANEL_WG_CONFIG'\n[Interface]\nPrivateKey = {privateKey}\n");
        if (port is not null) s.AppendLine($"ListenPort = {port}");
        foreach (var peer in peers)
        {
            s.AppendLine($"[Peer]\nPublicKey = {peer.PublicKey}\nAllowedIPs = {peer.AllowedIps}");
            if (peer.Endpoint is not null) s.AppendLine($"Endpoint = {peer.Endpoint}");
            if (peer.Keepalive is not null) s.AppendLine($"PersistentKeepalive = {peer.Keepalive}");
        }
        s.AppendLine("HYPANEL_WG_CONFIG");
        return s.ToString();
    }

    private static WireGuardEgressOptions Parse(string? json)
    {
        WireGuardEgressOptions? value;
        try { value = json is null ? null : JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.WireGuardEgressOptions); }
        catch (JsonException) { throw new InvalidOperationException("WireGuard 配置无效。"); }
        if (value is null || value.Port is < 1 or > 65535 ||
            !(value.InterfaceName == "hpwx" || value.InterfaceName is not null && value.InterfaceName.Length == 15 &&
                value.InterfaceName.StartsWith("hpw", StringComparison.Ordinal) &&
                value.InterfaceName[3..].All(Uri.IsHexDigit)) ||
            value.PrivateKey is null || !ValidKey(value.PrivateKey) || value.PeerPublicKey is not null && !ValidKey(value.PeerPublicKey))
            throw new InvalidOperationException("WireGuard 配置无效。");
        return value;
    }

    private static bool ValidKey(string key)
    {
        try { var bytes = Convert.FromBase64String(key); return bytes.Length == 32 && Convert.ToBase64String(bytes) == key; }
        catch (FormatException) { return false; }
    }

    private sealed record Peer(string PublicKey, string AllowedIps, string? Endpoint, int? Keepalive);
}
