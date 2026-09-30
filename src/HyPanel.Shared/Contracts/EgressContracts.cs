namespace HyPanel.Shared.Contracts;

/// <summary>Exit configuration shared by all transports. The initial implementation carries IPv4 payloads.</summary>
public sealed record EgressNetworkState(bool Enabled, IReadOnlyList<EgressTunnel> Tunnels,
    string Transport = EgressTransports.Gre, string? TransportOptionsJson = null, BackendArtifact? WireGuardTool = null);
public sealed record EgressNetworkReport(bool Enabled, bool Ready, string? Error,
    IReadOnlyList<string>? SupportedTransports = null, string AppliedTransport = EgressTransports.Gre, int AppliedUdpPort = 0);
public sealed record EgressTunnel(Guid ServiceId, int Slot, bool IsExit, string RemoteIpv4,
    string Transport = EgressTransports.Gre, string? TransportOptionsJson = null);
public sealed record ServiceEgressRoute(int Slot);

public static class EgressAddressing
{
    public static bool IsValidSlot(int slot) => slot is >= 1 and <= 16000;
    public static string Interface(int slot) => $"hpe{slot}";
    public static int Table(int slot) => 100000 + slot;
    public static int Priority(int slot) => 10000 + slot;
    public static uint Key(int slot) => 0x48500000u + (uint)slot;
    public static string Source(int slot) => Address(slot, 1);
    public static string Exit(int slot) => Address(slot, 2);
    private static string Address(int slot, int host)
    {
        if (!IsValidSlot(slot)) throw new ArgumentOutOfRangeException(nameof(slot));
        var address = (slot + 63) * 4 + host;
        return $"169.254.{address >> 8}.{address & 255}";
    }
}

/// <summary>Stable identifiers; declaring an identifier does not advertise an implemented backend.</summary>
public static class EgressTransports
{
    public const string Gre = "gre";
    public const string GreUdp = "gre-udp";
    public const string WireGuard = "wireguard";
    public const string GretapOverUdp = "gretap-udp";
    public static EgressTransportDefinition GreDefinition { get; } = new(Gre, "GRE", false, "ip", 47, 1400);
    public const int GreUdpPort = 47541;
    public static EgressTransportDefinition GreUdpDefinition { get; } = new(GreUdp, "GRE over UDP / FOU", false, "udp", null, 1392);
    public const int WireGuardPort = 51820;
    public static EgressTransportDefinition WireGuardDefinition { get; } = new(WireGuard, "WireGuard", true, "udp", null, 1360);
    // Extend this catalog when a transport is implemented; the frontend does not hardcode its options.
    public static IReadOnlyList<EgressTransportDefinition> Available { get; } = [GreDefinition, GreUdpDefinition, WireGuardDefinition];
}

public sealed record EgressTransportDefinition(string Id, string Name, bool Encrypted,
    string OuterTransport, int? IpProtocol, int Mtu);
