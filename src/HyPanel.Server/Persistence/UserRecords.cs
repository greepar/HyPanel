namespace HyPanel.Server.Persistence;

internal sealed record UserRecord(Guid Id, string Username, string NormalizedUsername, string Role, bool Enabled, long? TrafficLimitBytes, DateTimeOffset? ExpiresAtUtc, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, int? TrafficResetDay = null);
internal sealed record UserIssue(UserRecord User, string SubscriptionToken);
internal sealed record SessionIssue(UserRecord User, string Token, DateTimeOffset ExpiresAtUtc);
internal sealed record UsageTotalRecord(Guid UserId, Guid ServiceId, long UploadBytes, long DownloadBytes, DateTimeOffset UpdatedAtUtc);
internal sealed record UserServiceCredentialRecord(Guid UserId, Guid ServiceId, string BackendType, string Credential,
    string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

internal sealed record SubscriptionUserInfo(long UploadBytes, long DownloadBytes, long? TrafficLimitBytes, DateTimeOffset? ExpiresAtUtc, string Username = "");
