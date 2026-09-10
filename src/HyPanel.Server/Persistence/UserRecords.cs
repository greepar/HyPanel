namespace HyPanel.Server.Persistence;

internal sealed record UserRecord(Guid Id, string Username, string NormalizedUsername, string Role, bool Enabled, long? TrafficLimitBytes, DateTimeOffset? ExpiresAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
internal sealed record UserIssue(UserRecord User, string SubscriptionToken);
internal sealed record SessionIssue(UserRecord User, string Token, DateTimeOffset ExpiresAtUtc);
internal sealed record UsageTotalRecord(Guid UserId, Guid ServiceId, long UploadBytes, long DownloadBytes, DateTimeOffset UpdatedAtUtc);
