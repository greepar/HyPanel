namespace HyPanel.Shared.Contracts;

public sealed record EgressUdpOptions(int Port);

// The server derives keys from its persistent master key and sends only the local private key to each Agent.
public sealed record WireGuardEgressOptions(int Port, string InterfaceName, string PrivateKey,
    string? PeerPublicKey = null);
