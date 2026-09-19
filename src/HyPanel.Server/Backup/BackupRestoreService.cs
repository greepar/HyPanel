namespace HyPanel.Server.Backup;

using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyPanel.Server.Persistence;
using HyPanel.Server.Security;
using Microsoft.Data.Sqlite;

internal sealed class BackupRestoreService
{
    public const int FormatVersion = 1;
    public const long MaximumArchiveBytes = 256L * 1024 * 1024;
    private const long MaximumDatabaseBytes = 1024L * 1024 * 1024;
    private const int RetentionCount = 5;
    private static readonly TimeSpan ValidationLifetime = TimeSpan.FromHours(1);
    private readonly IConfiguration configuration;
    private readonly SqliteConnectionFactory connections;
    private readonly ProxyCredentialProtector protector;
    private readonly TimeProvider time;
    private readonly ILogger<BackupRestoreService> logger;
    private readonly ServerOperationCoordinator operations;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> activeDownloads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ValidatedBackup> validations = new(StringComparer.Ordinal);

    public BackupRestoreService(IConfiguration configuration, SqliteConnectionFactory connections,
        ProxyCredentialProtector protector, TimeProvider time, ILogger<BackupRestoreService> logger,
        ServerOperationCoordinator? operations = null)
    {
        this.configuration = configuration;
        this.connections = connections;
        this.protector = protector;
        this.time = time;
        this.logger = logger;
        this.operations = operations ?? new ServerOperationCoordinator();
        Directory.CreateDirectory(BackupDirectory);
        Directory.CreateDirectory(StagingDirectory);
        SetDirectoryMode(BackupDirectory);
        SetDirectoryMode(StagingDirectory);
    }

    public string BackupDirectory => Path.Combine(ServerDataDirectory.Resolve(configuration), "backups");
    public string StagingDirectory => Path.Combine(BackupDirectory, ".staging");
    public string PendingRestorePath => Path.Combine(ServerDataDirectory.Resolve(configuration), "pending-restore.json");
    public string RestoreStatusPath => Path.Combine(ServerDataDirectory.Resolve(configuration), "restore-status.json");

    public async Task<IReadOnlyList<BackupSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var result = new List<BackupSummary>();
        foreach (var path in Directory.EnumerateFiles(BackupDirectory, "hypanel-*.tar.gz", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ReadManifestOnlyAsync(path, cancellationToken);
                result.Add(new BackupSummary(Path.GetFileName(path), manifest.CreatedAtUtc, new FileInfo(path).Length,
                    manifest.HyPanelVersion, manifest.SchemaVersion));
            }
            catch (BackupException) { }
        }
        return result.OrderByDescending(static item => item.CreatedAtUtc).ToArray();
    }

    public async Task<BackupSummary> CreateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            var id = $"hypanel-backup-{now:yyyyMMdd-HHmmss-fffffff}.tar.gz";
            var path = ResolveBackupPath(id);
            var snapshot = Path.Combine(StagingDirectory, $"snapshot-{Guid.NewGuid():N}.sqlite");
            var temporary = path + ".tmp";
            try
            {
                await CreateSnapshotAsync(connections.DatabasePath, snapshot, cancellationToken);
                var manifest = await InspectDatabaseAsync(snapshot, now, cancellationToken);
                await WriteArchiveAsync(temporary, snapshot, manifest, cancellationToken);
                File.Move(temporary, path);
                SetFileMode(path);
                await ApplyRetentionAsync(id, cancellationToken);
                var summary = new BackupSummary(id, now, new FileInfo(path).Length, manifest.HyPanelVersion,
                    manifest.SchemaVersion);
                logger.LogInformation("backup_created BackupId={BackupId} Size={Size} Schema={Schema}",
                    id, summary.SizeBytes, summary.SchemaVersion);
                return summary;
            }
            finally
            {
                TryDelete(snapshot);
                TryDelete(temporary);
            }
        }
        finally { gate.Release(); }
    }

    public DownloadLease OpenDownload(string id)
    {
        var path = ResolveBackupPath(id);
        if (!File.Exists(path)) throw new FileNotFoundException();
        if (!activeDownloads.TryAdd(id, 0)) throw new BackupException("backup_busy");
        try
        {
            return new DownloadLease(id, new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read),
                activeDownloads);
        }
        catch
        {
            activeDownloads.TryRemove(id, out _);
            throw;
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (activeDownloads.ContainsKey(id)) throw new BackupException("backup_busy");
            var path = ResolveBackupPath(id);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            logger.LogInformation("backup_deleted BackupId={BackupId}", id);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<BackupValidation> UploadAndValidateAsync(Stream input, long? contentLength,
        CancellationToken cancellationToken)
    {
        if (contentLength is > MaximumArchiveBytes) return Invalid("archive_too_large");
        CleanupExpiredValidations();
        var validationId = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(StagingDirectory, validationId);
        Directory.CreateDirectory(directory);
        SetDirectoryMode(directory);
        var archive = Path.Combine(directory, "backup.tar.gz");
        try
        {
            await CopyBoundedAsync(input, archive, MaximumArchiveBytes, cancellationToken);
            SetFileMode(archive);
            var validated = await ValidateArchiveAsync(validationId, archive, directory, cancellationToken);
            validations[validationId] = validated;
            return ToValidation(validated, true, null);
        }
        catch (BackupException exception)
        {
            TryDeleteDirectory(directory);
            return Invalid(exception.Code, validationId);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or JsonException or SqliteException)
        {
            TryDeleteDirectory(directory);
            return Invalid("invalid_backup_archive", validationId);
        }
    }

    public async Task<BackupValidation> ValidateFileAsync(string archivePath, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath)) return Invalid("backup_not_found");
        await using var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await UploadAndValidateAsync(input, input.Length, cancellationToken);
    }

    public async Task QueueRestoreAsync(RestoreRequest request, CancellationToken cancellationToken)
    {
        if (request.Confirmation != "RESTORE") throw new BackupException("restore_confirmation_required");
        if (!operations.TryBegin("restore")) throw new BackupException("server_operation_in_progress");
        CleanupExpiredValidations();
        try
        {
            if (!validations.TryRemove(request.ValidationId, out var validated))
                throw new BackupException("restore_validation_expired");
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(PendingRestorePath)) throw new BackupException("restore_already_pending");
                await ValidateCandidateAsync(validated.Manifest, validated.DatabasePath, cancellationToken);
                var pending = new PendingRestore(validated.ValidationId, validated.DatabasePath, validated.ArchivePath,
                    time.GetUtcNow());
                await WriteJsonAtomicAsync(PendingRestorePath, pending, BackupJsonSerializerContext.Default.PendingRestore,
                    cancellationToken);
                await WriteRestoreStatusAsync(new RestoreStatus("RestartPending", null, null, time.GetUtcNow()),
                    cancellationToken);
                logger.LogInformation("restore_started ValidationId={ValidationId} Schema={Schema}",
                    validated.ValidationId, validated.Manifest.SchemaVersion);
            }
            finally { gate.Release(); }
        }
        catch
        {
            operations.End("restore");
            throw;
        }
    }

    public async Task AbortQueuedRestoreAsync(string error)
    {
        TryDelete(PendingRestorePath);
        await WriteRestoreStatusAsync(new RestoreStatus("Failed", null, error, time.GetUtcNow()),
            CancellationToken.None);
        operations.End("restore");
        logger.LogError("restore_failed Error={Error}", error);
    }

    public async Task<bool> ApplyPendingRestoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PendingRestorePath)) return false;
        PendingRestore? pending;
        try
        {
            pending = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(PendingRestorePath, cancellationToken),
                BackupJsonSerializerContext.Default.PendingRestore);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            await WriteRestoreStatusAsync(new RestoreStatus("Failed", null, "pending_restore_invalid",
                time.GetUtcNow()), cancellationToken);
            TryDelete(PendingRestorePath);
            logger.LogError(exception, "restore_failed Error={Error}", "pending_restore_invalid");
            return false;
        }
        if (pending is null) return false;

        var emergencySnapshot = Path.Combine(StagingDirectory, $"emergency-{Guid.NewGuid():N}.sqlite");
        var validationDirectory = Path.Combine(StagingDirectory, $"apply-{Guid.NewGuid():N}");
        try
        {
            var originalDirectory = Path.GetDirectoryName(pending.CandidateDatabasePath)
                ?? throw new BackupException("pending_restore_invalid");
            Directory.CreateDirectory(validationDirectory);
            SetDirectoryMode(validationDirectory);
            var validated = await ValidateArchiveAsync(pending.ValidationId, pending.ArchivePath, validationDirectory,
                cancellationToken);
            await CreateSnapshotAsync(connections.DatabasePath, emergencySnapshot, cancellationToken);
            var emergencyManifest = await InspectDatabaseAsync(emergencySnapshot, time.GetUtcNow(), cancellationToken);
            var emergencyId = $"hypanel-pre-restore-{time.GetUtcNow():yyyyMMdd-HHmmss-fffffff}.tar.gz";
            var emergencyPath = ResolveBackupPath(emergencyId);
            await WriteArchiveAsync(emergencyPath + ".tmp", emergencySnapshot, emergencyManifest, cancellationToken);
            File.Move(emergencyPath + ".tmp", emergencyPath);
            SetFileMode(emergencyPath);

            await ReplaceDatabaseAsync(validated.DatabasePath, cancellationToken);
            try
            {
                await ValidateLiveDatabaseAsync(cancellationToken);
            }
            catch
            {
                await ReplaceDatabaseAsync(emergencySnapshot, cancellationToken);
                throw;
            }

            TryDelete(PendingRestorePath);
            TryDeleteDirectory(originalDirectory);
            await WriteRestoreStatusAsync(new RestoreStatus("Succeeded", emergencyId, null, time.GetUtcNow()),
                cancellationToken);
            logger.LogInformation("restore_succeeded EmergencyBackupId={BackupId} Schema={Schema}", emergencyId,
                validated.Manifest.SchemaVersion);
            return true;
        }
        catch (Exception exception)
        {
            TryDelete(PendingRestorePath);
            await WriteRestoreStatusAsync(new RestoreStatus("Failed", null,
                exception is BackupException backup ? backup.Code : "restore_failed", time.GetUtcNow()),
                CancellationToken.None);
            logger.LogError(exception, "restore_failed Error={Error}",
                exception is BackupException value ? value.Code : "restore_failed");
            return false;
        }
        finally
        {
            TryDelete(emergencySnapshot);
            TryDeleteDirectory(validationDirectory);
        }
    }

    public async Task RestoreFileOfflineAsync(string archivePath, CancellationToken cancellationToken)
    {
        var validation = await ValidateFileAsync(archivePath, cancellationToken);
        if (!validation.Valid) throw new BackupException(validation.Error ?? "restore_validation_failed");
        await QueueRestoreAsync(new RestoreRequest(validation.ValidationId, "RESTORE"), cancellationToken);
        if (!await ApplyPendingRestoreAsync(cancellationToken)) throw new BackupException("restore_failed");
    }

    private async Task<ValidatedBackup> ValidateArchiveAsync(string validationId, string archivePath, string directory,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(archivePath).Length > MaximumArchiveBytes) throw new BackupException("archive_too_large");
        BackupManifest? manifest = null;
        var databasePath = Path.Combine(directory, "database.sqlite");
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var archive = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var count = 0;
        while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            if (++count > 2 || !names.Add(entry.Name)) throw new BackupException("duplicate_or_unknown_archive_entry");
            if (entry.EntryType is not TarEntryType.RegularFile || entry.DataStream is null
                || entry.Name is not ("manifest.json" or "database.sqlite"))
                throw new BackupException("invalid_archive_entry");
            if (entry.Name == "manifest.json")
            {
                using var memory = new MemoryStream();
                await CopyBoundedAsync(entry.DataStream, memory, 64 * 1024, cancellationToken);
                manifest = JsonSerializer.Deserialize(memory.ToArray(), BackupJsonSerializerContext.Default.BackupManifest)
                    ?? throw new BackupException("manifest_invalid");
            }
            else
            {
                await CopyBoundedAsync(entry.DataStream, databasePath, MaximumDatabaseBytes, cancellationToken);
                SetFileMode(databasePath);
            }
        }
        if (manifest is null || !names.SetEquals(["manifest.json", "database.sqlite"]) || !File.Exists(databasePath))
            throw new BackupException("incomplete_archive");
        await ValidateCandidateAsync(manifest, databasePath, cancellationToken);
        return new ValidatedBackup(validationId, archivePath, databasePath, manifest, time.GetUtcNow());
    }

    private async Task ValidateCandidateAsync(BackupManifest manifest, string databasePath,
        CancellationToken cancellationToken)
    {
        if (manifest.FormatVersion != FormatVersion) throw new BackupException("backup_format_unsupported");
        var info = new FileInfo(databasePath);
        if (manifest.DatabaseSizeBytes != info.Length) throw new BackupException("database_size_mismatch");
        var hash = await Sha256Async(databasePath, cancellationToken);
        if (!hash.Equals(manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            throw new BackupException("database_sha256_mismatch");
        var inspected = await InspectDatabaseCoreAsync(databasePath, cancellationToken);
        if (inspected.SchemaVersion != manifest.SchemaVersion) throw new BackupException("schema_manifest_mismatch");
        if (inspected.SchemaVersion > SqliteMigrationRunner.CurrentSchemaVersion)
            throw new BackupException("backup_schema_newer_than_server");
        if (manifest.HasEncryptedSecrets != inspected.HasEncryptedSecrets)
            throw new BackupException("encrypted_secret_manifest_mismatch");
        if (inspected.HasEncryptedSecrets)
        {
            var currentFingerprint = protector.MasterKeyFingerprint;
            if (manifest.MasterKeyFingerprint is not { Length: 64 } backupFingerprint
                || currentFingerprint is not { Length: 64 }
                || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(backupFingerprint), Encoding.ASCII.GetBytes(currentFingerprint)))
                throw new BackupException("master_key_mismatch");
        }
        await ValidateSecretsAsync(databasePath, inspected.SchemaVersion, cancellationToken);
    }

    private async Task<BackupManifest> InspectDatabaseAsync(string path, DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var inspected = await InspectDatabaseCoreAsync(path, cancellationToken);
        if (inspected.HasEncryptedSecrets && protector.MasterKeyFingerprint is null)
            throw new BackupException("master_key_required");
        return new BackupManifest(FormatVersion, createdAt, ServerBuildInfo.Version, inspected.SchemaVersion,
            new FileInfo(path).Length, await Sha256Async(path, cancellationToken),
            inspected.HasEncryptedSecrets ? protector.MasterKeyFingerprint : null, inspected.HasEncryptedSecrets);
    }

    private async Task<DatabaseInspection> InspectDatabaseCoreAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = connections.CreateConnection(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        await using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            if (!string.Equals((string?)await integrity.ExecuteScalarAsync(cancellationToken), "ok",
                    StringComparison.Ordinal)) throw new BackupException("database_integrity_failed");
        }
        if (!await TableExistsAsync(connection, "schema_migrations", cancellationToken))
            throw new BackupException("schema_metadata_missing");
        var versions = new List<long>();
        await using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT version FROM schema_migrations ORDER BY version;";
            await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) versions.Add(reader.GetInt64(0));
        }
        if (versions.Count == 0 || versions.Where((version, index) => version != index + 1).Any())
            throw new BackupException("schema_metadata_invalid");
        var version = versions[^1];
        var requiredTables = new List<string> { "nodes", "agents", "enrollment_tokens", "agent_commands" };
        if (version >= 3) requiredTables.AddRange(["service_instances", "service_runtime_states"]);
        if (version >= 4) requiredTables.AddRange(["users", "user_sessions", "user_service_bindings", "usage_batches", "usage_totals"]);
        if (version >= 5) requiredTables.Add("service_public_endpoints");
        if (version >= 6) requiredTables.Add("service_templates");
        if (version >= 9) requiredTables.Add("user_service_credentials");
        if (version >= 11) requiredTables.Add("global_settings");
        if (version >= 12) requiredTables.Add("certificates");
        foreach (var table in requiredTables)
            if (!await TableExistsAsync(connection, table, cancellationToken))
                throw new BackupException("candidate_repository_invalid");
        var credentials = version >= 9 && await HasRowsAsync(connection, "user_service_credentials", cancellationToken);
        var certificates = version >= 12 && await HasRowsAsync(connection, "certificates", cancellationToken);
        return new DatabaseInspection(version, credentials || certificates);
    }

    private async Task ValidateSecretsAsync(string path, long schemaVersion, CancellationToken cancellationToken)
    {
        await using var connection = connections.CreateConnection(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        if (schemaVersion >= 9)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT user_id,service_id,backend_type,nonce,ciphertext,tag FROM user_service_credentials;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string value;
                try
                {
                    value = protector.Unprotect(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                        reader.GetString(2), new ProtectedCredential((byte[])reader[3], (byte[])reader[4], (byte[])reader[5]));
                }
                catch (Exception exception) when (exception is InvalidOperationException or FormatException)
                {
                    throw new BackupException("encrypted_credential_invalid", exception);
                }
                if (string.IsNullOrWhiteSpace(value)) throw new BackupException("encrypted_credential_invalid");
            }
        }
        if (schemaVersion >= 12)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,key_nonce,key_ciphertext,key_tag FROM certificates;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string value;
                try
                {
                    value = protector.UnprotectCertificateKey(Guid.Parse(reader.GetString(0)),
                        new ProtectedCredential((byte[])reader[1], (byte[])reader[2], (byte[])reader[3]));
                }
                catch (Exception exception) when (exception is InvalidOperationException or FormatException)
                {
                    throw new BackupException("encrypted_certificate_key_invalid", exception);
                }
                if (!value.Contains("PRIVATE KEY-----", StringComparison.Ordinal))
                    throw new BackupException("encrypted_certificate_key_invalid");
            }
        }
    }

    private async Task ValidateLiveDatabaseAsync(CancellationToken cancellationToken)
    {
        await new SqliteMigrationRunner(connections, time).MigrateAsync(cancellationToken);
        var inspected = await InspectDatabaseCoreAsync(connections.DatabasePath, cancellationToken);
        if (inspected.SchemaVersion != SqliteMigrationRunner.CurrentSchemaVersion)
            throw new BackupException("migration_validation_failed");
        await ValidateSecretsAsync(connections.DatabasePath, inspected.SchemaVersion, cancellationToken);
    }

    private async Task ReplaceDatabaseAsync(string source, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        var target = connections.DatabasePath;
        var temporary = target + ".restore-tmp";
        TryDelete(temporary);
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Flush(true);
        }
        SetFileMode(temporary);
        TryDelete(target + "-wal");
        TryDelete(target + "-shm");
        File.Move(temporary, target, true);
    }

    private async Task CreateSnapshotAsync(string sourcePath, string destinationPath,
        CancellationToken cancellationToken)
    {
        TryDelete(destinationPath);
        await using var source = connections.CreateConnection(sourcePath, SqliteOpenMode.ReadOnly);
        await using var destination = connections.CreateConnection(destinationPath);
        await source.OpenAsync(cancellationToken);
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        await destination.CloseAsync();
        SetFileMode(destinationPath);
    }

    private static async Task WriteArchiveAsync(string path, string databasePath, BackupManifest manifest,
        CancellationToken cancellationToken)
    {
        TryDelete(path);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest,
                BackupJsonSerializerContext.Default.BackupManifest);
            await using (var manifestStream = new MemoryStream(manifestBytes, writable: false))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "manifest.json")
                    { DataStream = manifestStream, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
                await writer.WriteEntryAsync(entry, cancellationToken);
            }
            await using (var database = File.OpenRead(databasePath))
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, "database.sqlite")
                    { DataStream = database, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
                await writer.WriteEntryAsync(entry, cancellationToken);
            }
        }
        await output.FlushAsync(cancellationToken);
        output.Flush(true);
        SetFileMode(path);
    }

    private async Task<BackupManifest> ReadManifestOnlyAsync(string path, CancellationToken cancellationToken)
    {
        await using var archive = File.OpenRead(path);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            if (entry.Name != "manifest.json" || entry.DataStream is null) continue;
            using var memory = new MemoryStream();
            await CopyBoundedAsync(entry.DataStream, memory, 64 * 1024, cancellationToken);
            return JsonSerializer.Deserialize(memory.ToArray(), BackupJsonSerializerContext.Default.BackupManifest)
                ?? throw new BackupException("manifest_invalid");
        }
        throw new BackupException("manifest_missing");
    }

    private async Task ApplyRetentionAsync(string currentId, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(BackupDirectory, "hypanel-backup-*.tar.gz")
            .Select(static path => new FileInfo(path)).OrderByDescending(static file => file.Name, StringComparer.Ordinal)
            .ToArray();
        foreach (var file in files.Skip(RetentionCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = file.Name;
            if (id != currentId && !activeDownloads.ContainsKey(id)) file.Delete();
        }
    }

    private string ResolveBackupPath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id != Path.GetFileName(id)
            || !id.StartsWith("hypanel-", StringComparison.Ordinal)
            || !id.EndsWith(".tar.gz", StringComparison.Ordinal)) throw new BackupException("invalid_backup_id");
        var path = Path.GetFullPath(Path.Combine(BackupDirectory, id));
        if (!path.StartsWith(Path.GetFullPath(BackupDirectory) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BackupException("invalid_backup_id");
        return path;
    }

    private void CleanupExpiredValidations()
    {
        var cutoff = time.GetUtcNow() - ValidationLifetime;
        foreach (var item in validations)
        {
            if (item.Value.ValidatedAtUtc >= cutoff || !validations.TryRemove(item.Key, out var removed)) continue;
            TryDeleteDirectory(Path.GetDirectoryName(removed.ArchivePath)!);
        }
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=@name);";
        command.Parameters.AddWithValue("@name", name);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0;
    }

    private static async Task<bool> HasRowsAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {table});";
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 0;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static async Task CopyBoundedAsync(Stream input, string path, long maximum,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await CopyBoundedAsync(input, output, maximum, cancellationToken);
        await output.FlushAsync(cancellationToken);
        output.Flush(true);
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > maximum) throw new BackupException("archive_entry_too_large");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private async Task WriteRestoreStatusAsync(RestoreStatus status, CancellationToken cancellationToken) =>
        await WriteJsonAtomicAsync(RestoreStatusPath, status, BackupJsonSerializerContext.Default.RestoreStatus,
            cancellationToken);

    private static async Task WriteJsonAtomicAsync<T>(string path, T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(value, typeInfo), cancellationToken);
        SetFileMode(temporary);
        File.Move(temporary, path, true);
    }

    private BackupValidation Invalid(string error, string? validationId = null) => new(validationId ?? string.Empty,
        false, error, null, null, null, null, null, false, error != "master_key_mismatch");

    private static BackupValidation ToValidation(ValidatedBackup value, bool valid, string? error) => new(
        value.ValidationId, valid, error, value.Manifest.FormatVersion, value.Manifest.CreatedAtUtc,
        value.Manifest.HyPanelVersion, value.Manifest.SchemaVersion, value.Manifest.DatabaseSizeBytes, true, true);

    private static void SetFileMode(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void SetDirectoryMode(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ValidatedBackup(string ValidationId, string ArchivePath, string DatabasePath,
        BackupManifest Manifest, DateTimeOffset ValidatedAtUtc);
    private sealed record DatabaseInspection(long SchemaVersion, bool HasEncryptedSecrets);

    internal sealed class DownloadLease(string id, FileStream stream,
        ConcurrentDictionary<string, byte> activeDownloads) : IDisposable
    {
        public Stream Stream => stream;
        public long Length => stream.Length;
        public void Dispose()
        {
            stream.Dispose();
            activeDownloads.TryRemove(id, out _);
        }
    }
}
