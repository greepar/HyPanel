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

/// <summary>
/// AnyTLS: TLS with the service's certificate, each user logging in with their own password (their credential). The
/// TLS handshake and its padding make it look like ordinary HTTPS.
/// </summary>
internal sealed class AnyTlsProtocol : ServiceProtocol
{
    public static AnyTlsProtocol Instance { get; } = new();

    public override string Id => "anytls";

    public override ListenTransport Transport => ListenTransport.Tcp;

    public override SubscriptionProxy Project(SubscriptionServiceRecord service, JsonElement config, string name,
        string credential)
    {
        var endpoint = service.PublicEndpoint;
        return new AnyTlsProxy(name, endpoint.Host, endpoint.Port, endpoint.TlsServerName, credential,
            service.PinnedCertificateSha256);
    }

    private sealed record AnyTlsProxy(
        string Name,
        string Host,
        int Port,
        string? TlsServerName,
        string Password,
        string? PinnedSha256) : SubscriptionProxy(Name, Host, Port)
    {
        public override void AppendMihomo(StringBuilder yaml)
        {
            yaml.Append("  - name: ").Append(Quote(Name)).Append("\n    type: anytls\n    server: ")
                .Append(Quote(Host)).Append("\n    port: ").Append(Port).Append("\n    password: ")
                .Append(Quote(Password)).Append("\n    udp: true\n    client-fingerprint: chrome\n")
                .Append(PinnedSha256 is null
                    ? "    skip-cert-verify: false\n"
                    : "    skip-cert-verify: true\n    fingerprint: " + Quote(PinnedSha256) + "\n");
            if (!string.IsNullOrEmpty(TlsServerName))
                yaml.Append("    sni: ").Append(Quote(TlsServerName)).Append('\n');
        }
    }
}

/// <summary>
/// Shadowsocks 2022 (<see cref="Shadowsocks2022.Cipher"/>) with sing-mux. TCP and UDP on the same port; each user sends
/// <c>serverKey:userKey</c>, the user key derived from their credential. Clients multiplex streams with h2mux.
/// </summary>
internal sealed class Shadowsocks2022Protocol : ServiceProtocol
{
    public static Shadowsocks2022Protocol Instance { get; } = new();

    public override string Id => "shadowsocks-2022";

    public override ListenTransport Transport => ListenTransport.Tcp | ListenTransport.Udp;

    public override SubscriptionProxy? Project(SubscriptionServiceRecord service, JsonElement config, string name,
        string credential)
    {
        if (!TryString(config, "serverKey", out var serverKey)) return null;
        var endpoint = service.PublicEndpoint;
        return new Shadowsocks2022Proxy(name, endpoint.Host, endpoint.Port,
            $"{serverKey}:{Shadowsocks2022.UserKey(credential)}");
    }

    private sealed record Shadowsocks2022Proxy(string Name, string Host, int Port, string Password)
        : SubscriptionProxy(Name, Host, Port)
    {
        public override void AppendMihomo(StringBuilder yaml) =>
            yaml.Append("  - name: ").Append(Quote(Name)).Append("\n    type: ss\n    server: ").Append(Quote(Host))
                .Append("\n    port: ").Append(Port).Append("\n    cipher: ").Append(Shadowsocks2022.Cipher)
                .Append("\n    password: ").Append(Quote(Password))
                .Append("\n    udp: true\n    smux:\n      enabled: true\n      protocol: h2mux\n");
    }
}
