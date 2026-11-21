using System.Text;
using System.Text.Json;
using HyPanel.Server.Persistence;
using HyPanel.Shared.Networking;

namespace HyPanel.Server.Backends;

/// <summary>Hysteria2 over QUIC. Each user authenticates as <c>hypanel-&lt;user&gt;:&lt;credential&gt;</c>.</summary>
internal sealed class Hysteria2Protocol : ServiceProtocol
{
    public static Hysteria2Protocol Instance { get; } = new();

    public override string Id => "hysteria2";

    public override ListenTransport Transport => ListenTransport.Udp;

    /// <summary>The userpass name of a user; every Hysteria2 engine authenticates users by this name.</summary>
    internal static string UserName(Guid userId) => $"hypanel-{userId:N}";

    public override SubscriptionProxy Project(SubscriptionServiceRecord service, JsonElement config, string name,
        string credential)
    {
        TryString(config, "obfsPassword", out var obfsPassword);
        var hopping = TryString(config, "portHopping", out var hopText) && PortSpec.TryParse(hopText, out var ports)
            ? ports.ToString()
            : null;
        var endpoint = service.PublicEndpoint;
        return new Hysteria2Proxy(name, endpoint.Host, endpoint.Port, endpoint.TlsServerName,
            $"{UserName(service.UserId)}:{credential}", obfsPassword.Length > 0 ? obfsPassword : null,
            service.PinnedCertificateSha256, hopping);
    }

    private sealed record Hysteria2Proxy(
        string Name,
        string Host,
        int Port,
        string? TlsServerName,
        string Password,
        string? ObfsPassword,
        string? PinnedSha256,
        string? PortHopping) : SubscriptionProxy(Name, Host, Port)
    {
        public override void AppendMihomo(StringBuilder yaml)
        {
            yaml.Append("  - name: ").Append(Quote(Name)).Append("\n    type: hysteria2\n    server: ")
                .Append(Quote(Host)).Append("\n    port: ").Append(Port).Append("\n    password: ")
                .Append(Quote(Password)).Append(PinnedSha256 is null
                    ? "\n    skip-cert-verify: false\n"
                    : "\n    skip-cert-verify: true\n    fingerprint: " + Quote(PinnedSha256) + "\n");
            if (!string.IsNullOrEmpty(TlsServerName))
                yaml.Append("    sni: ").Append(Quote(TlsServerName)).Append('\n');
            if (PortHopping is not null)
                yaml.Append("    ports: ").Append(Quote(PortHopping)).Append("\n    hop-interval: 30\n");
            if (!string.IsNullOrEmpty(ObfsPassword))
                yaml.Append("    obfs: salamander\n    obfs-password: ").Append(Quote(ObfsPassword)).Append('\n');
        }
    }
}

/// <summary>VLESS with XTLS Vision over TCP, disguised by a REALITY handshake. Each user dials their own UUID.</summary>
internal sealed class VlessRealityProtocol : ServiceProtocol
{
    public static VlessRealityProtocol Instance { get; } = new();

    public override string Id => "vless-reality";

    public override ListenTransport Transport => ListenTransport.Tcp;

    public override SubscriptionProxy? Project(SubscriptionServiceRecord service, JsonElement config, string name,
        string credential)
    {
        if (!TryString(config, "flow", out var flow) ||
            !TryString(config, "realityPublicKey", out var publicKey) ||
            !TryString(config, "shortId", out var shortId) ||
            !TryString(config, "serverName", out var serverName) ||
            !TryString(config, "fingerprint", out var fingerprint)) return null;
        var endpoint = service.PublicEndpoint;
        return new VlessRealityProxy(name, endpoint.Host, endpoint.Port, credential, flow, serverName, fingerprint,
            publicKey, shortId);
    }

    private sealed record VlessRealityProxy(
        string Name,
        string Host,
        int Port,
        string ClientId,
        string Flow,
        string ServerName,
        string Fingerprint,
        string PublicKey,
        string ShortId) : SubscriptionProxy(Name, Host, Port)
    {
        public override void AppendMihomo(StringBuilder yaml) =>
            yaml.Append("  - name: ").Append(Quote(Name)).Append("\n    type: vless\n    server: ")
                .Append(Quote(Host)).Append("\n    port: ").Append(Port).Append("\n    uuid: ")
                .Append(Quote(ClientId)).Append("\n    flow: ").Append(Quote(Flow))
                .Append("\n    tls: true\n    servername: ").Append(Quote(ServerName))
                .Append("\n    client-fingerprint: ").Append(Quote(Fingerprint))
                .Append("\n    reality-opts:\n      public-key: ").Append(Quote(PublicKey))
                .Append("\n      short-id: ").Append(Quote(ShortId))
                // Recent REALITY servers require a hybrid key share; Mihomo removes it by default.
                .Append("\n      support-x25519mlkem768: true\n");
    }
}
