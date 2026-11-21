using System.Text;
using System.Text.Json;
using HyPanel.Server.Persistence;

namespace HyPanel.Server.Backends;

/// <summary>What a backend can do; the Panel, the Agent sync and the editor read these instead of backend names.</summary>
[Flags]
internal enum BackendFeatures
{
    None = 0,

    /// <summary>One identity per granted user, with per-user traffic; such services receive user grants.</summary>
    MultiUser = 1 << 0,

    /// <summary>Needs a loopback port for the engine's local stats API.</summary>
    ControlPort = 1 << 1,

    /// <summary>Extra lines (routes) on the same port, each with its own exit.</summary>
    Routes = 1 << 2,

    /// <summary>Another node's Agent can forward a TCP port to the service.</summary>
    Relay = 1 << 3,

    /// <summary>Needs a TLS certificate from the Panel (<c>certificateId</c>).</summary>
    Certificate = 1 << 4,

    /// <summary>Supports UDP port hopping (<c>portHopping</c>), redirected by the Agent's firewall rules.</summary>
    PortHopping = 1 << 5,

    /// <summary>Can obtain its certificate through ACME by itself.</summary>
    AcmeCertificate = 1 << 6
}

/// <summary>The transport a service listens on; port conflicts are checked per transport.</summary>
[Flags]
internal enum ListenTransport
{
    Tcp = 1,
    Udp = 2
}

/// <summary>
/// The protocol clients speak to a service, independent of the engine (program) that serves it on the node: Hysteria2
/// is the same to a client whether the official server or Xray runs it. A protocol turns a service and a user's
/// credential into a subscription entry; adding a protocol means adding one subclass and registering it in
/// <see cref="BackendDefinitionCatalog"/>.
/// </summary>
internal abstract class ServiceProtocol
{
    /// <summary>Stable protocol id, e.g. <c>hysteria2</c> or <c>vless-reality</c>.</summary>
    public abstract string Id { get; }

    public abstract ListenTransport Transport { get; }

    /// <summary>
    /// The subscription entry for one user of the service under <paramref name="name"/>, authenticating with
    /// <paramref name="credential"/> (the user's credential, or the derived one of a route); null when the stored
    /// config is incomplete.
    /// </summary>
    public abstract SubscriptionProxy? Project(SubscriptionServiceRecord service, JsonElement config, string name,
        string credential);

    /// <summary>A non-empty string property, matched case-insensitively.</summary>
    protected static bool TryString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) ||
                property.Value.ValueKind != JsonValueKind.String) continue;
            value = property.Value.GetString() ?? string.Empty;
            return value.Length > 0;
        }

        return false;
    }
}

/// <summary>One entry of a user's subscription, written in the client format it knows.</summary>
internal abstract record SubscriptionProxy(string Name, string Host, int Port)
{
    /// <summary>Appends this entry to a Mihomo <c>proxies:</c> list (two-space indented list item).</summary>
    public abstract void AppendMihomo(StringBuilder yaml);

    /// <summary>A double-quoted YAML scalar.</summary>
    public static string Quote(string value) => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
}
