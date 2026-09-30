namespace HyPanel.Agent.Networking;

using System.Text;
using HyPanel.Shared.Contracts;

/// <summary>Unencrypted keyed layer-three GRE, implemented by the Linux kernel.</summary>
public sealed class GreEgressTransportBackend : IEgressTransportBackend
{
    public EgressTransportDefinition Definition => EgressTransports.GreDefinition;

    public string PrepareExit(string? optionsJson)
    {
        ValidateOptions(optionsJson);
        return "ip link show hpeprobe >/dev/null 2>&1 || ip link add hpeprobe type gre local 127.0.0.1 remote 127.0.0.2 key 1213202432 || { echo '内核不支持 GRE 或 Agent 缺少 NET_ADMIN 权限，请加载 ip_gre 并检查节点安装。' >&2; exit 1; }\nip link delete hpeprobe\n";
    }

    public string ConfigureTunnel(EgressTunnel tunnel)
    {
        ValidateOptions(tunnel.TransportOptionsJson);
        var name = EgressAddressing.Interface(tunnel.Slot);
        var script = new StringBuilder();
        script.AppendLine($"local_ip=$(ip -4 route get {tunnel.RemoteIpv4} | awk '{{for(i=1;i<=NF;i++) if($i==\"src\") {{print $(i+1); exit}}}}')");
        script.AppendLine("[ -n \"$local_ip\" ] || { echo '无法确定隧道本地地址' >&2; exit 1; }");
        script.AppendLine($"if ip link show {name} >/dev/null 2>&1; then ip link set dev {name} type gre local \"$local_ip\" remote {tunnel.RemoteIpv4} key {EgressAddressing.Key(tunnel.Slot)} ttl 64 encap none; else ip link add {name} type gre local \"$local_ip\" remote {tunnel.RemoteIpv4} key {EgressAddressing.Key(tunnel.Slot)} ttl 64 encap none; fi");
        return script.ToString();
    }

    public string InputFirewallRule(EgressTunnel tunnel) => $"ip saddr {tunnel.RemoteIpv4} ip protocol gre accept";

    private static void ValidateOptions(string? options)
    {
        if (options is not null && options.Trim() != "{}")
            throw new InvalidOperationException("GRE 后端不接受额外的隧道参数。");
    }
}
