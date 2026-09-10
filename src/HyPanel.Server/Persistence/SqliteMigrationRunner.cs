namespace HyPanel.Server.Persistence;

using Microsoft.Data.Sqlite;

internal sealed class SqliteMigrationRunner(SqliteConnectionFactory connectionFactory, TimeProvider timeProvider)
{
    private const long InitialSchemaVersion = 1;
    private const long CommandExpirySchemaVersion = 2;
    private const long ServiceInstancesSchemaVersion = 3;
    private const long UsersAndUsageSchemaVersion = 4;
    private const long ServicePublicEndpointsSchemaVersion = 5;
    private const long ServiceTemplatesSchemaVersion = 6;

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(connection, transaction, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at_utc TEXT NOT NULL
            );
            """, cancellationToken);

        if (!await IsAppliedAsync(connection, transaction, InitialSchemaVersion, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE nodes (
                    id TEXT NOT NULL PRIMARY KEY,
                    display_name TEXT NOT NULL,
                    desired_revision INTEGER NOT NULL CHECK (desired_revision >= 0),
                    created_at_utc TEXT NOT NULL
                );

                CREATE TABLE agents (
                    id TEXT NOT NULL PRIMARY KEY,
                    node_id TEXT NOT NULL UNIQUE,
                    secret_hash BLOB NOT NULL,
                    enrolled_at_utc TEXT NOT NULL,
                    last_seen_at_utc TEXT NULL,
                    reported_version TEXT NULL,
                    reported_platform TEXT NULL,
                    applied_revision INTEGER NOT NULL CHECK (applied_revision >= 0),
                    latest_metric_snapshot_json TEXT NULL,
                    FOREIGN KEY (node_id) REFERENCES nodes (id) ON DELETE RESTRICT
                );

                CREATE TABLE enrollment_tokens (
                    id TEXT NOT NULL PRIMARY KEY,
                    token_hash BLOB NOT NULL UNIQUE,
                    node_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    expires_at_utc TEXT NOT NULL,
                    consumed_at_utc TEXT NULL,
                    consumed_by_agent_id TEXT NULL,
                    FOREIGN KEY (node_id) REFERENCES nodes (id) ON DELETE RESTRICT,
                    FOREIGN KEY (consumed_by_agent_id) REFERENCES agents (id) ON DELETE RESTRICT
                );

                CREATE TABLE agent_commands (
                    id TEXT NOT NULL PRIMARY KEY,
                    agent_id TEXT NOT NULL,
                    type TEXT NOT NULL,
                    status TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    started_at_utc TEXT NULL,
                    completed_at_utc TEXT NULL,
                    error_code TEXT NULL,
                    error_message TEXT NULL,
                    FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE RESTRICT
                );

                CREATE INDEX ix_agent_commands_agent_status_created
                    ON agent_commands (agent_id, status, created_at_utc);
                CREATE INDEX ix_enrollment_tokens_node_id ON enrollment_tokens (node_id);
                """, cancellationToken);

            await using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations (version, applied_at_utc) VALUES (@version, @appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("@version", InitialSchemaVersion);
            insertMigration.Parameters.AddWithValue("@appliedAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            await insertMigration.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsAppliedAsync(connection, transaction, CommandExpirySchemaVersion, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, "ALTER TABLE agent_commands ADD COLUMN expires_at_utc TEXT NULL;", cancellationToken);
            await ExecuteAsync(connection, transaction, "CREATE INDEX ix_agent_commands_agent_type_status_expiry ON agent_commands (agent_id, type, status, expires_at_utc);", cancellationToken);

            await using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations (version, applied_at_utc) VALUES (@version, @appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("@version", CommandExpirySchemaVersion);
            insertMigration.Parameters.AddWithValue("@appliedAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            await insertMigration.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsAppliedAsync(connection, transaction, ServiceInstancesSchemaVersion, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE service_instances (
                    id TEXT NOT NULL PRIMARY KEY,
                    node_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    backend_type TEXT NOT NULL,
                    backend_version TEXT NOT NULL,
                    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                    config_schema_version INTEGER NOT NULL CHECK (config_schema_version >= 0),
                    config_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL,
                    UNIQUE (node_id, name),
                    FOREIGN KEY (node_id) REFERENCES nodes (id) ON DELETE RESTRICT
                );

                CREATE TABLE service_runtime_states (
                    service_id TEXT NOT NULL PRIMARY KEY,
                    status INTEGER NOT NULL,
                    backend_version TEXT NULL,
                    applied_config_sha256 TEXT NULL,
                    traffic_upload_bytes INTEGER NULL,
                    traffic_download_bytes INTEGER NULL,
                    traffic_observed_at_utc TEXT NULL,
                    observed_at_utc TEXT NOT NULL,
                    error_code TEXT NULL,
                    error_message TEXT NULL,
                    FOREIGN KEY (service_id) REFERENCES service_instances (id) ON DELETE RESTRICT
                );

                CREATE INDEX ix_service_instances_node_id ON service_instances (node_id);
                """, cancellationToken);

            await using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations (version, applied_at_utc) VALUES (@version, @appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("@version", ServiceInstancesSchemaVersion);
            insertMigration.Parameters.AddWithValue("@appliedAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            await insertMigration.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsAppliedAsync(connection, transaction, UsersAndUsageSchemaVersion, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE users (
                    id TEXT NOT NULL PRIMARY KEY, username TEXT NOT NULL,
                    normalized_username TEXT NOT NULL UNIQUE COLLATE BINARY,
                    password_hash TEXT NOT NULL, role TEXT NOT NULL CHECK (role IN ('Admin', 'User')),
                    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
                    traffic_limit_bytes INTEGER NULL CHECK (traffic_limit_bytes >= 0),
                    expires_at_utc TEXT NULL, subscription_token_hash BLOB NOT NULL UNIQUE,
                    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL
                );
                CREATE TABLE user_sessions (
                    id TEXT NOT NULL PRIMARY KEY, user_id TEXT NOT NULL, token_hash BLOB NOT NULL UNIQUE,
                    created_at_utc TEXT NOT NULL, expires_at_utc TEXT NOT NULL, revoked_at_utc TEXT NULL,
                    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT
                );
                CREATE INDEX ix_user_sessions_user_expiry ON user_sessions (user_id, expires_at_utc);
                CREATE TABLE user_service_bindings (
                    user_id TEXT NOT NULL, service_id TEXT NOT NULL, created_at_utc TEXT NOT NULL,
                    PRIMARY KEY (user_id, service_id),
                    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,
                    FOREIGN KEY (service_id) REFERENCES service_instances (id) ON DELETE RESTRICT
                );
                CREATE INDEX ix_user_service_bindings_service ON user_service_bindings (service_id);
                CREATE TABLE usage_batches (
                    agent_id TEXT NOT NULL, batch_id TEXT NOT NULL, observed_at_utc TEXT NOT NULL, accepted_at_utc TEXT NOT NULL,
                    PRIMARY KEY (agent_id, batch_id),
                    FOREIGN KEY (agent_id) REFERENCES agents (id) ON DELETE RESTRICT
                );
                CREATE TABLE usage_totals (
                    user_id TEXT NOT NULL, service_id TEXT NOT NULL,
                    upload_bytes INTEGER NOT NULL CHECK (upload_bytes >= 0), download_bytes INTEGER NOT NULL CHECK (download_bytes >= 0), updated_at_utc TEXT NOT NULL,
                    PRIMARY KEY (user_id, service_id),
                    FOREIGN KEY (user_id) REFERENCES users (id) ON DELETE RESTRICT,
                    FOREIGN KEY (service_id) REFERENCES service_instances (id) ON DELETE RESTRICT
                );
                """, cancellationToken);
            await using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations (version, applied_at_utc) VALUES (@version, @appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("@version", UsersAndUsageSchemaVersion);
            insertMigration.Parameters.AddWithValue("@appliedAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            await insertMigration.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsAppliedAsync(connection, transaction, ServicePublicEndpointsSchemaVersion, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE service_public_endpoints (
                    service_id TEXT NOT NULL PRIMARY KEY,
                    host TEXT NOT NULL,
                    port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
                    tls_server_name TEXT NULL,
                    updated_at_utc TEXT NOT NULL,
                    FOREIGN KEY (service_id) REFERENCES service_instances (id) ON DELETE RESTRICT
                );
                """, cancellationToken);
            await using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations (version, applied_at_utc) VALUES (@version, @appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("@version", ServicePublicEndpointsSchemaVersion);
            insertMigration.Parameters.AddWithValue("@appliedAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            await insertMigration.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await IsAppliedAsync(connection, transaction, ServiceTemplatesSchemaVersion, cancellationToken))
        {
            await ExecuteAsync(connection, transaction, """
                CREATE TABLE service_templates (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL,
                    normalized_name TEXT NOT NULL UNIQUE COLLATE BINARY,
                    backend_type TEXT NOT NULL CHECK (backend_type IN ('hysteria2', 'xray', 'mihomo', 'sing-box')),
                    backend_version TEXT NOT NULL,
                    config_schema_version INTEGER NOT NULL CHECK (config_schema_version > 0),
                    config_json TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """, cancellationToken);
            await using var insertMigration = connection.CreateCommand();
            insertMigration.Transaction = transaction;
            insertMigration.CommandText = "INSERT INTO schema_migrations (version, applied_at_utc) VALUES (@version, @appliedAtUtc);";
            insertMigration.Parameters.AddWithValue("@version", ServiceTemplatesSchemaVersion);
            insertMigration.Parameters.AddWithValue("@appliedAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            await insertMigration.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> IsAppliedAsync(SqliteConnection connection, SqliteTransaction transaction, long version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM schema_migrations WHERE version = @version;";
        command.Parameters.AddWithValue("@version", version);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
