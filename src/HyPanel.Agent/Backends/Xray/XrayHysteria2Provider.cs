using System.Text.Json;
using System.Text.Json.Nodes;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;
using HyPanel.Agent.Backends.Infrastructure;

namespace HyPanel.Agent.Backends.Xray;

/// <summary>
/// Hysteria2 on the Xray engine (backend type <c>xray-hysteria2</c>). Clients cannot tell it from the official server:
/// users authenticate as <c>hypanel-&lt;user&gt;:&lt;credential&gt;</c>, masquerade, Salamander, Brutal bandwidth
/// and port hopping (redirected by the Agent) work the same. Unlike the official server, Xray routes by user, so the
/// service can carry extra lines.
/// </summary>
internal sealed class XrayHysteria2Provider(TimeProvider timeProvider, BackendInstanceStore instanceStore)
    : XrayEngineProvider<XrayHysteria2Config>(timeProvider), IPortHoppingBackend
{
    private const int MaximumBandwidthMbps = 100_000;

    public override string BackendType => "xray-hysteria2";

    protected override bool ListensOnUdp => true;

    protected override XrayHysteria2Config? Parse(string configJson) =>
        JsonSerializer.Deserialize(configJson, XrayJsonSerializerContext.Default.XrayHysteria2Config);

    protected override bool IsValidProtocolConfig(XrayHysteria2Config config, ServiceDesiredState desiredState) =>
        // Xray cannot issue ACME certificates; it needs a stored (uploaded or on-disk) one.
        desiredState.TlsCertificate is { Kind: TlsCertificateKinds.Upload or TlsCertificateKinds.Path } &&
        config.CertificateId != Guid.Empty &&
        IsSecret(config.AuthPassword) &&
        Uri.TryCreate(config.MasqueradeUrl, UriKind.Absolute, out var masquerade) &&
        masquerade.Scheme is "http" or "https" &&
        (config.ObfsPassword is null || IsSecret(config.ObfsPassword)) &&
        config.UpMbps is >= 1 and <= MaximumBandwidthMbps &&
        config.DownMbps is >= 1 and <= MaximumBandwidthMbps &&
        (string.IsNullOrWhiteSpace(config.PortHopping) || PortSpec.TryParse(config.PortHopping, out _));

    protected override JsonNode BuildInbound(XrayHysteria2Config config, IReadOnlyList<XrayClientIdentity> clients,
        ServiceDesiredState desiredState)
    {
        var tls = desiredState.TlsCertificate!;
        // Xray resolves relative certificate paths against its own binary's directory, so they are made absolute.
        var tlsDirectory = Path.Combine(instanceStore.GetInstanceDirectory(desiredState.ServiceId), "tls", tls.Fingerprint);
        var (certificateFile, keyFile) = tls.Kind == TlsCertificateKinds.Path
            ? (tls.CertificatePath!, tls.PrivateKeyPath!)
            : (Path.Combine(tlsDirectory, "cert.pem"), Path.Combine(tlsDirectory, "key.pem"));
        var users = new JsonArray();
        foreach (var client in clients)
            users.Add((JsonNode)new JsonObject
            {
                ["auth"] = $"{UserName(client.UserId)}:{client.Credential}",
                ["email"] = client.Email,
                ["level"] = 0
            });
        var hysteria = new JsonObject
        {
            ["version"] = 2,
            ["masquerade"] = new JsonObject
            {
                ["type"] = "proxy", ["url"] = config.MasqueradeUrl, ["rewriteHost"] = true
            }
        };
        // Without granted users the service still answers its shared password, like the official server.
        if (clients.Count == 0) hysteria["auth"] = config.AuthPassword;
        var finalMask = new JsonObject
        {
            // The server's Brutal rates: what it sends (up) and accepts (down) per connection at most.
            ["quicParams"] = new JsonObject
            {
                ["congestion"] = "brutal",
                ["brutalUp"] = $"{config.UpMbps} mbps",
                ["brutalDown"] = $"{config.DownMbps} mbps"
            }
        };
        if (config.ObfsPassword is not null)
            finalMask["udp"] = new JsonArray(new JsonObject
            {
                ["type"] = "salamander", ["settings"] = new JsonObject { ["password"] = config.ObfsPassword }
            });
        return new JsonObject
        {
            ["listen"] = config.ListenHost,
            ["port"] = config.ListenPort,
            ["protocol"] = "hysteria",
            ["settings"] = new JsonObject { ["version"] = 2, ["users"] = users },
            ["streamSettings"] = new JsonObject
            {
                ["network"] = "hysteria",
                ["security"] = "tls",
                ["tlsSettings"] = new JsonObject
                {
                    ["alpn"] = new JsonArray("h3"),
                    ["certificates"] = new JsonArray(new JsonObject
                    {
                        ["certificateFile"] = certificateFile, ["keyFile"] = keyFile
                    })
                },
                ["hysteriaSettings"] = hysteria,
                ["finalmask"] = finalMask
            }
        };
    }

    public (int ListenPort, PortSpec Ports)? PortHopping(string configJson)
    {
        try
        {
            return Parse(configJson) is { } config && !string.IsNullOrWhiteSpace(config.PortHopping)
                   && PortSpec.TryParse(config.PortHopping, out var ports)
                ? (config.ListenPort, ports)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The userpass name every Hysteria2 engine authenticates a user by.</summary>
    private static string UserName(Guid userId) => $"hypanel-{userId:N}";

    private static bool IsSecret(string? value) =>
        value is { Length: >= 8 and <= 256 } && !value.Any(char.IsControl);
}
