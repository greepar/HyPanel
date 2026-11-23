using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyPanel.Shared.Contracts;

namespace HyPanel.Agent.Backends.Xray;

/// <summary>VLESS + XTLS Vision + REALITY on the Xray engine (backend type <c>xray</c>).</summary>
internal sealed class XrayProvider(TimeProvider timeProvider) : XrayEngineProvider<XrayConfig>(timeProvider)
{
    private const string VisionFlow = "xtls-rprx-vision";

    public override string BackendType => "xray";

    protected override bool ListensOnUdp => false;

    protected override XrayConfig? Parse(string configJson) =>
        JsonSerializer.Deserialize(configJson, XrayJsonSerializerContext.Default.XrayConfig);

    protected override bool IsValidProtocolConfig(XrayConfig config, ServiceDesiredState desiredState) =>
        config.Flow == VisionFlow &&
        RealityValidation.IsKey(config.RealityPrivateKey) &&
        RealityValidation.IsKey(config.RealityPublicKey) &&
        RealityValidation.IsShortId(config.ShortId) &&
        IsDnsHostname(config.ServerName) &&
        RealityValidation.IsDestination(config.Destination) &&
        RealityValidation.IsFingerprint(config.Fingerprint);

    protected override JsonNode BuildInbound(XrayConfig config, IReadOnlyList<XrayClientIdentity> clients,
        ServiceDesiredState desiredState) => ToNode(new XrayInbound
    {
        Listen = config.ListenHost!,
        Port = config.ListenPort,
        Protocol = "vless",
        Settings = new XrayInboundSettings
        {
            Clients = [.. clients.Select(client => new XrayClient
            {
                Id = client.Credential, Email = client.Email, Flow = config.Flow!, Level = 0
            })],
            Decryption = "none"
        },
        StreamSettings = new XrayStreamSettings
        {
            Network = "tcp",
            Security = "reality",
            RealitySettings = new XrayRealitySettings
            {
                Show = false,
                Dest = config.Destination!,
                Xver = 0,
                ServerNames = [config.ServerName!],
                PrivateKey = config.RealityPrivateKey!,
                ShortIds = [config.ShortId!]
            }
        }
    }, XrayRuntimeJsonSerializerContext.Default.XrayInbound);
}
