namespace HyPanel.Shared.Contracts;

public sealed record AgentEnrollmentRequest(
    string Token,
    string AgentVersion,
    string Platform,
    AgentMachineInfo Machine);

public sealed record AgentMachineInfo(string Hostname);

public sealed record AgentEnrollmentResponse(
    Guid AgentId,
    string AgentSecret,
    Guid NodeId,
    int SyncIntervalSeconds);
