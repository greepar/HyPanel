using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using HyPanel.Server.Backup;
using HyPanel.Server.Persistence;
using HyPanel.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Backup;

[TestClass]
public sealed class BackupRestoreServiceTests
{
    [TestMethod]
    public async Task CreateAsync_IncludesWalCommittedRowsAndListsManifestMetadata()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, "wal-committed", CancellationToken.None);

        var backup = await fixture.Service.CreateAsync(CancellationToken.None);
        var listed = await fixture.Service.ListAsync(CancellationToken.None);

        Assert.AreEqual(1, listed.Count);
        Assert.AreEqual(backup.Id, listed[0].Id);
        Assert.AreEqual(fixture.Time.GetUtcNow(), listed[0].CreatedAtUtc);
        Assert.AreEqual(backup.SizeBytes, listed[0].SizeBytes);
        Assert.AreEqual(ServerBuildInfo.Version, listed[0].ServerVersion);
        Assert.AreEqual(SqliteMigrationRunner.CurrentSchemaVersion, listed[0].SchemaVersion);

        using var archive = OpenArchive(Path.Combine(fixture.Service.BackupDirectory, backup.Id));
        var manifest = await ReadManifestAsync(archive);
        Assert.AreEqual(BackupRestoreService.FormatVersion, manifest.FormatVersion);
        Assert.AreEqual(backup.CreatedAtUtc, manifest.CreatedAtUtc);
        Assert.AreEqual(backup.SchemaVersion, manifest.SchemaVersion);
        Assert.IsFalse(manifest.HasEncryptedSecrets);
        var database = await ReadDatabaseAsync(archive);
        await using var snapshot = new SqliteConnection($"Data Source={database};Mode=ReadOnly");
        await snapshot.OpenAsync();
        await using var command = snapshot.CreateCommand();
        command.CommandText = "SELECT display_name FROM nodes WHERE id=@id;";
        command.Parameters.AddWithValue("@id", nodeId.ToString("D"));
        Assert.AreEqual("wal-committed", await command.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task CreateAsync_AppliesFiveBackupRetention()
    {
        await using var fixture = await TestDatabase.CreateAsync();

        for (var i = 0; i < 7; i++)
        {
            await fixture.Service.CreateAsync(CancellationToken.None);
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.AreEqual(5, (await fixture.Service.ListAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task OpenDownload_RejectsTraversalBackupId()
    {
        await using var fixture = await TestDatabase.CreateAsync();

        var exception = Assert.ThrowsException<BackupException>(() =>
            fixture.Service.OpenDownload("../hypanel-backup-escape.tar.gz"));

        Assert.AreEqual("invalid_backup_id", exception.Code);
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_RejectsOversizedUpload()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var input = new MemoryStream();

        var result = await fixture.Service.UploadAndValidateAsync(input,
            BackupRestoreService.MaximumArchiveBytes + 1, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("archive_too_large", result.Error);
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_RejectsInvalidTar()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        using var input = new MemoryStream("not a gzip tar"u8.ToArray());

        var result = await fixture.Service.UploadAndValidateAsync(input, input.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("invalid_backup_archive", result.Error);
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_RejectsShaMismatch()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, manifest => manifest with
        {
            DatabaseSha256 = new string('0', 64)
        });

        var result = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("database_sha256_mismatch", result.Error);
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_RejectsDuplicateArchiveEntry()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, duplicateManifest: true);

        var result = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("duplicate_or_unknown_archive_entry", result.Error);
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_RejectsArchivePathTraversal()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, databaseEntryName: "../database.sqlite");

        var result = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("invalid_archive_entry", result.Error);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Service.BackupDirectory, "database.sqlite")));
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_RejectsIncompleteArchive()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, includeDatabase: false);

        var result = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("incomplete_archive", result.Error);
    }

    [TestMethod]
    public async Task CreateAsync_RetentionDoesNotDeleteActiveDownload()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var oldest = await fixture.Service.CreateAsync(CancellationToken.None);
        using var download = fixture.Service.OpenDownload(oldest.Id);
        for (var index = 0; index < 5; index++)
        {
            fixture.Time.Advance(TimeSpan.FromSeconds(1));
            await fixture.Service.CreateAsync(CancellationToken.None);
        }

        Assert.IsTrue(File.Exists(Path.Combine(fixture.Service.BackupDirectory, oldest.Id)));
        Assert.AreEqual(6, (await fixture.Service.ListAsync(CancellationToken.None)).Count);
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_RejectsNewerSchema()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO schema_migrations(version, applied_at_utc) VALUES (@version, @now);";
            command.Parameters.AddWithValue("@version", SqliteMigrationRunner.CurrentSchemaVersion + 1);
            command.Parameters.AddWithValue("@now", fixture.Time.GetUtcNow().ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, manifest => manifest with
        {
            SchemaVersion = SqliteMigrationRunner.CurrentSchemaVersion + 1
        });

        var result = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("backup_schema_newer_than_server", result.Error);
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_RejectsMasterKeyMismatch()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var userId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO users
                    (id, username, normalized_username, password_hash, role, enabled, subscription_token_hash,
                     created_at_utc, updated_at_utc)
                VALUES (@user, 'backup-user', 'backup-user', 'hash', 'User', 1, @token_hash, @now, @now);
                INSERT INTO nodes (id, display_name, desired_revision, created_at_utc)
                VALUES (@node, 'Backup Node', 0, @now);
                INSERT INTO service_instances
                    (id, node_id, name, backend_type, backend_version, enabled, config_schema_version, config_json,
                     created_at_utc, updated_at_utc)
                VALUES (@service, @node, 'Backup Service', 'xray', '1.0', 1, 1, '{}', @now, @now);
                INSERT INTO user_service_credentials
                    (user_id, service_id, backend_type, nonce, ciphertext, tag, status, desired_eligible,
                     created_at_utc, updated_at_utc)
                VALUES (@user, @service, 'xray', @nonce, @ciphertext, @tag, 'Active', 1, @now, @now);
                """;
            command.Parameters.AddWithValue("@user", userId.ToString("D"));
            command.Parameters.AddWithValue("@node", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@service", serviceId.ToString("D"));
            command.Parameters.AddWithValue("@nonce", new byte[12]);
            command.Parameters.AddWithValue("@ciphertext", new byte[] { 1, 2, 3 });
            command.Parameters.AddWithValue("@tag", new byte[16]);
            command.Parameters.AddWithValue("@token_hash", SHA256.HashData(Guid.NewGuid().ToByteArray()));
            command.Parameters.AddWithValue("@now", fixture.Time.GetUtcNow().ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, masterKeyFingerprint: fixture.Protector.MasterKeyFingerprint);
        var otherKeyConfiguration = fixture.CreateConfigurationWithMasterKey(
            Convert.ToBase64String(Enumerable.Repeat((byte)0xA5, 32).ToArray()));
        var otherService = new BackupRestoreService(otherKeyConfiguration, fixture.ConnectionFactory,
            new ProxyCredentialProtector(otherKeyConfiguration), fixture.Time, NullLogger<BackupRestoreService>.Instance);

        var result = await otherService.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("master_key_mismatch", result.Error);
        Assert.IsFalse(result.MasterKeyCompatible);
    }

    [TestMethod]
    public async Task ApplyPendingRestoreAsync_ValidBackupRestoresOriginalDatabase()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var originalId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(originalId, "before-backup", CancellationToken.None);
        var backup = await fixture.Service.CreateAsync(CancellationToken.None);
        var archivePath = Path.Combine(fixture.Service.BackupDirectory, backup.Id);
        await fixture.Repository.CreateNodeAsync(Guid.NewGuid(), "after-backup", CancellationToken.None);
        await using var stream = File.OpenRead(archivePath);
        var validation = await fixture.Service.UploadAndValidateAsync(stream, stream.Length, CancellationToken.None);

        await fixture.Service.QueueRestoreAsync(new RestoreRequest(validation.ValidationId, "RESTORE"),
            CancellationToken.None);
        Assert.IsTrue(await fixture.Service.ApplyPendingRestoreAsync(CancellationToken.None));

        await using var restored = await fixture.OpenConnectionAsync();
        await using var command = restored.CreateCommand();
        command.CommandText = "SELECT group_concat(display_name, ',') FROM nodes;";
        Assert.AreEqual("before-backup", await command.ExecuteScalarAsync());
        Assert.AreEqual(SqliteMigrationRunner.CurrentSchemaVersion,
            await ReadSchemaVersionAsync(restored));
        Assert.AreEqual(1, Directory.EnumerateFiles(fixture.Service.BackupDirectory,
            "hypanel-pre-restore-*.tar.gz").Count());
    }

    [TestMethod]
    public async Task ApplyPendingRestoreAsync_OlderSchemaMigratesToCurrent()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "ALTER TABLE agents DROP COLUMN public_ipv4; DROP TABLE certificates; DROP TABLE revoked_agents; ALTER TABLE users DROP COLUMN subscription_token_nonce; ALTER TABLE users DROP COLUMN subscription_token_ciphertext; ALTER TABLE users DROP COLUMN subscription_token_tag; DROP TABLE user_group_services; DROP TABLE user_groups; ALTER TABLE users DROP COLUMN group_id; ALTER TABLE global_settings DROP COLUMN mihomo_template; ALTER TABLE service_instances DROP COLUMN control_port; DELETE FROM schema_migrations WHERE version IN (12,13,14,15,16,17,18,19,20);";
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, manifest => manifest with { SchemaVersion = 11 });
        var validation = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);
        Assert.IsTrue(validation.Valid, validation.Error);

        await fixture.Service.QueueRestoreAsync(new RestoreRequest(validation.ValidationId, "RESTORE"),
            CancellationToken.None);
        Assert.IsTrue(await fixture.Service.ApplyPendingRestoreAsync(CancellationToken.None));

        await using var restored = await fixture.OpenConnectionAsync();
        Assert.AreEqual(SqliteMigrationRunner.CurrentSchemaVersion, await ReadSchemaVersionAsync(restored));
        await using var table = restored.CreateCommand();
        table.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='certificates');";
        Assert.AreEqual(1L, await table.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task ApplyPendingRestoreAsync_MigrationFailureRollsBackOriginalDatabase()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var originalId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(originalId, "must-survive", CancellationToken.None);
        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var mutation = connection.CreateCommand())
        {
            mutation.CommandText =
                "ALTER TABLE agents DROP COLUMN public_ipv4; DELETE FROM schema_migrations WHERE version IN (12,13,14,15,16,17,18,19,20); DELETE FROM nodes;";
            await mutation.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var invalidCandidate = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(invalidCandidate, manifest => manifest with { SchemaVersion = 11 });
        var validation = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);
        Assert.IsTrue(validation.Valid, validation.Error);

        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var restoreVersion = connection.CreateCommand())
        {
            restoreVersion.CommandText =
                "INSERT INTO schema_migrations(version,applied_at_utc) VALUES (12,@now),(13,@now),(14,@now),(15,@now),(16,@now),(17,@now),(18,@now),(19,@now),(20,@now);";
            restoreVersion.Parameters.AddWithValue("@now", fixture.Time.GetUtcNow().ToString("O"));
            await restoreVersion.ExecuteNonQueryAsync();
        }
        await fixture.Repository.CreateNodeAsync(originalId, "must-survive", CancellationToken.None);
        await fixture.Service.QueueRestoreAsync(new RestoreRequest(validation.ValidationId, "RESTORE"),
            CancellationToken.None);
        Assert.IsFalse(await fixture.Service.ApplyPendingRestoreAsync(CancellationToken.None));

        await using var restored = await fixture.OpenConnectionAsync();
        await using var command = restored.CreateCommand();
        command.CommandText = "SELECT display_name FROM nodes WHERE id=@id;";
        command.Parameters.AddWithValue("@id", originalId.ToString("D"));
        Assert.AreEqual("must-survive", await command.ExecuteScalarAsync());
        Assert.AreEqual(SqliteMigrationRunner.CurrentSchemaVersion, await ReadSchemaVersionAsync(restored));
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_CorruptCredentialWithMatchingMasterKeyIsRejected()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await InsertCredentialAsync(fixture, new ProtectedCredential(new byte[12], [1, 2, 3], new byte[16]));
        SqliteConnection.ClearAllPools();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, masterKeyFingerprint: fixture.Protector.MasterKeyFingerprint);

        var result = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("encrypted_credential_invalid", result.Error);
    }

    [TestMethod]
    public async Task UploadAndValidateAsync_CorruptCertificateKeyWithMatchingMasterKeyIsRejected()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO certificates
                  (id,name,normalized_name,certificate_pem,key_nonce,key_ciphertext,key_tag,created_at_utc,
                   not_before_utc,expires_at_utc,fingerprint,subject,san)
                VALUES (@id,'bad-cert','BAD-CERT','CERT',@nonce,@cipher,@tag,@now,@now,@expires,@fingerprint,'CN=bad','');
                """;
            command.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("@nonce", new byte[12]);
            command.Parameters.AddWithValue("@cipher", new byte[] { 1, 2, 3 });
            command.Parameters.AddWithValue("@tag", new byte[16]);
            command.Parameters.AddWithValue("@now", fixture.Time.GetUtcNow().ToString("O"));
            command.Parameters.AddWithValue("@expires", fixture.Time.GetUtcNow().AddDays(1).ToString("O"));
            command.Parameters.AddWithValue("@fingerprint", new string('a', 64));
            await command.ExecuteNonQueryAsync();
        }
        SqliteConnection.ClearAllPools();
        var database = await File.ReadAllBytesAsync(fixture.ConnectionFactory.DatabasePath);
        using var archive = CreateArchive(database, masterKeyFingerprint: fixture.Protector.MasterKeyFingerprint);

        var result = await fixture.Service.UploadAndValidateAsync(archive, archive.Length, CancellationToken.None);

        Assert.IsFalse(result.Valid);
        Assert.AreEqual("encrypted_certificate_key_invalid", result.Error);
    }

    private static async Task InsertCredentialAsync(TestDatabase fixture, ProtectedCredential protectedCredential)
    {
        var userId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users
              (id,username,normalized_username,password_hash,role,enabled,subscription_token_hash,created_at_utc,updated_at_utc)
            VALUES (@user,'credential-user','CREDENTIAL-USER','hash','User',1,@token,@now,@now);
            INSERT INTO nodes (id,display_name,desired_revision,created_at_utc) VALUES (@node,'credential-node',0,@now);
            INSERT INTO service_instances
              (id,node_id,name,backend_type,backend_version,enabled,config_schema_version,config_json,created_at_utc,updated_at_utc)
            VALUES (@service,@node,'credential-service','xray','1',1,1,'{}',@now,@now);
            INSERT INTO user_service_credentials
              (user_id,service_id,backend_type,nonce,ciphertext,tag,status,desired_eligible,created_at_utc,updated_at_utc)
            VALUES (@user,@service,'xray',@nonce,@cipher,@tag,'Active',1,@now,@now);
            """;
        command.Parameters.AddWithValue("@user", userId.ToString("D"));
        command.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        command.Parameters.AddWithValue("@node", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@token", SHA256.HashData(Guid.NewGuid().ToByteArray()));
        command.Parameters.AddWithValue("@nonce", protectedCredential.Nonce);
        command.Parameters.AddWithValue("@cipher", protectedCredential.Ciphertext);
        command.Parameters.AddWithValue("@tag", protectedCredential.Tag);
        command.Parameters.AddWithValue("@now", fixture.Time.GetUtcNow().ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ReadSchemaVersionAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static MemoryStream CreateArchive(byte[] database, Func<BackupManifest, BackupManifest>? updateManifest = null,
        bool duplicateManifest = false, string? masterKeyFingerprint = null, bool includeDatabase = true,
        string databaseEntryName = "database.sqlite")
    {
        var manifest = new BackupManifest(BackupRestoreService.FormatVersion,
            DateTimeOffset.Parse("2026-09-19T12:00:00Z"), ServerBuildInfo.Version,
            SqliteMigrationRunner.CurrentSchemaVersion, database.Length,
            Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant(), masterKeyFingerprint,
            masterKeyFingerprint is not null);
        if (updateManifest is not null) manifest = updateManifest(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, BackupJsonSerializerContext.Default.BackupManifest);
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            WriteEntry(writer, "manifest.json", bytes);
            if (duplicateManifest) WriteEntry(writer, "manifest.json", bytes);
            if (includeDatabase) WriteEntry(writer, databaseEntryName, database);
        }
        output.Position = 0;
        return output;
    }

    private static void WriteEntry(TarWriter writer, string name, byte[] bytes) =>
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) });

    private static TarReader OpenArchive(string path)
    {
        var stream = File.OpenRead(path);
        var gzip = new GZipStream(stream, CompressionMode.Decompress);
        return new TarReader(gzip);
    }

    private static async Task<BackupManifest> ReadManifestAsync(TarReader reader)
    {
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            if (entry.Name != "manifest.json") continue;
            using var memory = new MemoryStream();
            await entry.DataStream!.CopyToAsync(memory);
            return JsonSerializer.Deserialize(memory.ToArray(), BackupJsonSerializerContext.Default.BackupManifest)!;
        }
        Assert.Fail("Archive manifest was missing.");
        return null!;
    }

    private static async Task<string> ReadDatabaseAsync(TarReader reader)
    {
        while (await reader.GetNextEntryAsync() is { } entry)
        {
            if (entry.Name != "database.sqlite") continue;
            var path = Path.Combine(Path.GetTempPath(), $"hypanel-backup-read-{Guid.NewGuid():N}.sqlite");
            await using var output = File.Create(path);
            await entry.DataStream!.CopyToAsync(output);
            return path;
        }
        Assert.Fail("Archive database was missing.");
        return string.Empty;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string directory;

        private TestDatabase(string directory, IConfiguration configuration, FakeTimeProvider time)
        {
            this.directory = directory;
            Configuration = configuration;
            Time = time;
            ConnectionFactory = new SqliteConnectionFactory(configuration);
            Protector = new ProxyCredentialProtector(configuration);
            Repository = new SqliteServerRepository(ConnectionFactory, time, Protector);
            Service = new BackupRestoreService(configuration, ConnectionFactory, Protector, time,
                NullLogger<BackupRestoreService>.Instance);
        }

        public IConfiguration Configuration { get; }
        public FakeTimeProvider Time { get; }
        public SqliteConnectionFactory ConnectionFactory { get; }
        public ProxyCredentialProtector Protector { get; }
        public SqliteServerRepository Repository { get; }
        public BackupRestoreService Service { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HyPanel.BackupRestoreTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-19T12:00:00Z"));
            var fixture = new TestDatabase(directory, CreateConfiguration(directory,
                Convert.ToBase64String(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray())), time);
            await new SqliteMigrationRunner(fixture.ConnectionFactory, time).MigrateAsync(CancellationToken.None);
            return fixture;
        }

        public IConfiguration CreateConfigurationWithMasterKey(string masterKey) =>
            CreateConfiguration(directory, masterKey);

        private static IConfiguration CreateConfiguration(string directory, string masterKey) =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HyPanel"] = $"Data Source={Path.Combine(directory, "server.db")}",
                ["HYPANEL_DATA_DIR"] = directory,
                ["HyPanel:Security:MasterKey"] = masterKey
            }).Build();

        public Task<SqliteConnection> OpenConnectionAsync() => ConnectionFactory.OpenAsync(CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current = current.Add(value);
    }
}
