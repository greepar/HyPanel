namespace HyPanel.Shared.Contracts;

public enum AgentCommandType
{
    RunHealthCheck = 0,
}

public enum AgentCommandStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
}

public sealed record AgentCommand(
    Guid CommandId,
    AgentCommandType Type,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

public sealed record AgentCommandResult(
    Guid CommandId,
    AgentCommandStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage);
