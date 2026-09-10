namespace HyPanel.Shared.Contracts;

public sealed record ApiError(
    string Code,
    string Message,
    string? TraceId = null);
