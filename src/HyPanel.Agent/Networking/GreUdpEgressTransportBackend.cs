namespace HyPanel.Agent.Networking;

using System.Text;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

/// <summary>Keyed layer-three GRE inside unencrypted UDP, using Linux FOU rather than a user-space relay.</summary>
public sealed class GreUdpEgressTransportBackend : IEgressTransportBackend
{
    public EgressTransportDefinition Definition => EgressTransports.GreUdpDefinition;

    public string PrepareExit(string? optionsJson)
    {
        var port = Port(optionsJson);
        return EnsureReceivePort(port) +
            $"ip link show hpeprobe >/dev/null 2>&1 || ip link add hpeprobe type gre local 127.0.0.1 remote 127.0.0.2 key 1213202432 encap fou encap-sport {port} encap-dport {port} || {{ echo '内核不支持 GRE over UDP (FOU) 或 Agent 缺少 NET_ADMIN 权限。' >&2; exit 1; }}\nip link delete hpeprobe\n";
    }

    public string ConfigureTunnel(EgressTunnel tunnel)
    {
        var name = EgressAddressing.Interface(tunnel.Slot);
        var port = Port(tunnel.TransportOptionsJson);
        var script = new StringBuilder(EnsureReceivePort(port));
        script.AppendLine($"local_ip=$(ip -4 route get {tunnel.RemoteIpv4} | awk '{{for(i=1;i<=NF;i++) if($i==\"src\") {{print $(i+1); exit}}}}')");
        script.AppendLine("[ -n \"$local_ip\" ] || { echo '无法确定隧道本地地址' >&2; exit 1; }");
        script.AppendLine($"if ip link show {name} >/dev/null 2>&1; then ip link set dev {name} type gre local \"$local_ip\" remote {tunnel.RemoteIpv4} key {EgressAddressing.Key(tunnel.Slot)} ttl 64 encap fou encap-sport {port} encap-dport {port}; else ip link add {name} type gre local \"$local_ip\" remote {tunnel.RemoteIpv4} key {EgressAddressing.Key(tunnel.Slot)} ttl 64 encap fou encap-sport {port} encap-dport {port}; fi");
        return script.ToString();
    }

    public string InputFirewallRule(EgressTunnel tunnel) =>
        $"ip saddr {tunnel.RemoteIpv4} ip protocol udp udp dport {Port(tunnel.TransportOptionsJson)} accept";

    internal static string EnsureReceivePort(int port) =>
        $"ip fou show | grep -Eq '^port {port} ipproto 47($| )' || ip fou add port {port} ipproto 47 || {{ echo '无法配置 GRE over UDP (FOU) 接收端口，请检查内核 FOU 支持及端口占用。' >&2; exit 1; }}\n";

    internal static int Port(string? options)
    {
        if (options is null || options.Trim() == "{}") return EgressTransports.GreUdpPort;
        EgressUdpOptions? value;
        try { value = JsonSerializer.Deserialize(options, HyPanelJsonSerializerContext.Default.EgressUdpOptions); }
        catch (JsonException) { throw new InvalidOperationException("GRE over UDP 端口配置无效。"); }
        if (value?.Port is not (>= 1 and <= 65535)) throw new InvalidOperationException("GRE over UDP 端口配置无效。");
        return value.Port;
    }
}
