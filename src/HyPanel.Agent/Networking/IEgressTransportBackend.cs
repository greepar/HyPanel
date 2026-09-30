namespace HyPanel.Agent.Networking;

using HyPanel.Shared.Contracts;

/// <summary>
/// Builds the transport-specific interface commands and outer-packet firewall matches.
/// The manager owns inner IPv4 addresses, policy routing, NAT, health checks and cleanup.
/// Implementations must validate their own options and quote any command arguments derived from options.
/// </summary>
public interface IEgressTransportBackend
{
    EgressTransportDefinition Definition { get; }
    string PrepareExit(string? optionsJson);
    string PrepareTunnels(IReadOnlyList<EgressTunnel> tunnels) => string.Empty;
    string Interface(EgressTunnel tunnel) => EgressAddressing.Interface(tunnel.Slot);
    string ConfigureTunnel(EgressTunnel tunnel);
    string InputFirewallRule(EgressTunnel tunnel);
}

public sealed class EgressTransportRegistry
{
    private readonly IReadOnlyDictionary<string, IEgressTransportBackend> backends;
    public EgressTransportRegistry(IEnumerable<IEgressTransportBackend> providers)
    {
        var map = new Dictionary<string, IEgressTransportBackend>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (!map.TryAdd(provider.Definition.Id, provider))
                throw new InvalidOperationException($"Duplicate exit transport: {provider.Definition.Id}.");
        }
        backends = map;
    }

    public IReadOnlyList<string> SupportedTransports => backends.Keys.Order(StringComparer.Ordinal).ToArray();
    public IEgressTransportBackend Resolve(string id) => backends.TryGetValue(id, out var backend)
        ? backend : throw new InvalidOperationException($"Agent 不支持出口后端 {id}，请更新 Agent 或选择已支持的后端。");

    public static EgressTransportRegistry Default { get; } = new([new GreEgressTransportBackend(), new GreUdpEgressTransportBackend(), new WireGuardEgressTransportBackend()]);
}
