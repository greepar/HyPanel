namespace HyPanel.Server.Backup;

internal sealed record BackupManifest(
    int FormatVersion,
    DateTimeOffset CreatedAtUtc,
    string HyPanelVersion,
    long SchemaVersion,
    long DatabaseSizeBytes,
    string DatabaseSha256,
    string? MasterKeyFingerprint,
    bool HasEncryptedSecrets);

internal sealed record BackupSummary(string Id, DateTimeOffset CreatedAtUtc, long SizeBytes,
    string ServerVersion, long SchemaVersion);

internal sealed record BackupValidation(string ValidationId, bool Valid, string? Error, int? FormatVersion,
    DateTimeOffset? CreatedAtUtc, string? ServerVersion, long? SchemaVersion, long? DatabaseSizeBytes,
    bool DatabaseIntegrity, bool MasterKeyCompatible);

internal sealed record RestoreRequest(string ValidationId, string Confirmation);
internal sealed record RestoreResponse(string Status);
internal sealed record PendingRestore(string ValidationId, string CandidateDatabasePath, string ArchivePath,
    DateTimeOffset CreatedAtUtc);
internal sealed record RestoreStatus(string Status, string? BackupId, string? Error, DateTimeOffset UpdatedAtUtc);

internal sealed class BackupException(string code, Exception? inner = null) : Exception(code, inner)
{
    public string Code { get; } = code;
}
