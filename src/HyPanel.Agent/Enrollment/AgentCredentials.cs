namespace HyPanel.Agent;

public sealed record AgentCredentials(
    string PanelBaseUrl,
    Guid AgentId,
    string AgentSecret,
    Guid NodeId,
    int SyncIntervalSeconds);
