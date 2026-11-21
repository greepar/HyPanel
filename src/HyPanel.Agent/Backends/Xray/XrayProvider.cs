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
        IsBase64Url32Bytes(config.RealityPrivateKey) &&
        IsBase64Url32Bytes(config.RealityPublicKey) &&
        IsShortId(config.ShortId) &&
        IsDnsHostname(config.ServerName) &&
        IsDestination(config.Destination) &&
        config.Fingerprint is "chrome" or "firefox" or "safari" or "edge" or "randomized";

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

    private static bool IsBase64Url32Bytes(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('=')
            || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            return Convert.FromBase64String(padded).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsShortId(string? value) =>
        value is { Length: >= 2 and <= 16 } && value.Length % 2 == 0 && value.All(char.IsAsciiHexDigitLower);

    /// <summary><c>host:port</c> or <c>[ipv6]:port</c> of the site REALITY borrows its handshake from.</summary>
    private static bool IsDestination(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string portText;
        if (value[0] == '[')
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket < 2 || closingBracket + 1 >= value.Length || value[closingBracket + 1] != ':')
                return false;
            var host = value[1..closingBracket];
            portText = value[(closingBracket + 2)..];
            if (!IsCanonicalIpAddress(host) || IPAddress.Parse(host).AddressFamily != AddressFamily.InterNetworkV6)
                return false;
        }
        else
        {
            var separator = value.LastIndexOf(':');
            if (separator < 1 || separator != value.IndexOf(':')) return false;
            var host = value[..separator];
            portText = value[(separator + 1)..];
            if (!(IsCanonicalIpAddress(host) || IsDnsHostname(host))) return false;
        }

        return int.TryParse(portText, out var port) && port is >= 1 and <= 65_535 && portText == port.ToString();
    }
}
