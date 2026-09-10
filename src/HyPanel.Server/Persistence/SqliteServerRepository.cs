namespace HyPanel.Server.Persistence;

using System.Security.Cryptography;
using HyPanel.Shared.Contracts;
using Microsoft.Data.Sqlite;

internal sealed partial class SqliteServerRepository(
    SqliteConnectionFactory connectionFactory,
    TimeProvider timeProvider)
{
    public async Task<NodeRecord> CreateNodeAsync(Guid id, string displayName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var createdAtUtc = timeProvider.GetUtcNow();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO nodes (id, display_name, desired_revision, created_at_utc)
                              VALUES (@id, @displayName, @desiredRevision, @createdAtUtc);
                              """;
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        command.Parameters.AddWithValue("@displayName", displayName);
        command.Parameters.AddWithValue("@desiredRevision", 0L);
        command.Parameters.AddWithValue("@createdAtUtc", SqliteValue.ToUtcText(createdAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new NodeRecord(id, displayName, 0, createdAtUtc);
    }

    public async Task<NodeRecord?> GetNodeAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, display_name, desired_revision, created_at_utc FROM nodes WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadNode(reader) : null;
    }

    public async Task<bool> TrySetNodeDesiredRevisionAsync(Guid nodeId, long desiredRevision,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(desiredRevision);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE nodes SET desired_revision = @desiredRevision WHERE id = @id;";
        command.Parameters.AddWithValue("@id", nodeId.ToString("D"));
        command.Parameters.AddWithValue("@desiredRevision", desiredRevision);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<EnrollmentTokenIssue> CreateEnrollmentTokenAsync(Guid tokenId, Guid nodeId, string plaintextToken,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextToken);

        var createdAtUtc = timeProvider.GetUtcNow();
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Expiration must be in the future.");
        }

        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintextToken));
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO enrollment_tokens (id, token_hash, node_id, created_at_utc, expires_at_utc, consumed_at_utc, consumed_by_agent_id)
                              VALUES (@id, @tokenHash, @nodeId, @createdAtUtc, @expiresAtUtc, NULL, NULL);
                              """;
        command.Parameters.AddWithValue("@id", tokenId.ToString("D"));
        command.Parameters.Add("@tokenHash", SqliteType.Blob).Value = tokenHash;
        command.Parameters.AddWithValue("@nodeId", nodeId.ToString("D"));
        command.Parameters.AddWithValue("@createdAtUtc", SqliteValue.ToUtcText(createdAtUtc));
        command.Parameters.AddWithValue("@expiresAtUtc", SqliteValue.ToUtcText(expiresAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new EnrollmentTokenIssue(
            new EnrollmentTokenRecord(tokenId, nodeId, createdAtUtc, expiresAtUtc, null, null),
            plaintextToken);
    }

    public async Task<EnrollmentTokenRecord?> GetEnrollmentTokenAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, node_id, created_at_utc, expires_at_utc, consumed_at_utc, consumed_by_agent_id
                              FROM enrollment_tokens
                              WHERE id = @id;
                              """;
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEnrollmentToken(reader) : null;
    }

    public async Task<AgentEnrollmentResult?> TryConsumeEnrollmentTokenAndCreateAgentAsync(
        string plaintextToken,
        Guid agentId,
        string plaintextAgentSecret,
        string reportedVersion,
        string reportedPlatform,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(plaintextAgentSecret);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedPlatform);

        var tokenHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintextToken));
        var secretHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(plaintextAgentSecret));
        var nowUtc = timeProvider.GetUtcNow();

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var nodeId = await GetEligibleTokenNodeIdAsync(connection, transaction, tokenHash, nowUtc, cancellationToken);
        if (nodeId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (var insertAgent = connection.CreateCommand())
        {
            insertAgent.Transaction = transaction;
            insertAgent.CommandText = """
                                      INSERT INTO agents (
                                          id, node_id, secret_hash, enrolled_at_utc, last_seen_at_utc,
                                          reported_version, reported_platform, applied_revision, latest_metric_snapshot_json)
                                      VALUES (@id, @nodeId, @secretHash, @enrolledAtUtc, NULL, @reportedVersion, @reportedPlatform, @appliedRevision, NULL);
                                      """;
            insertAgent.Parameters.AddWithValue("@id", agentId.ToString("D"));
            insertAgent.Parameters.AddWithValue("@nodeId", nodeId.Value.ToString("D"));
            insertAgent.Parameters.Add("@secretHash", SqliteType.Blob).Value = secretHash;
            insertAgent.Parameters.AddWithValue("@enrolledAtUtc", SqliteValue.ToUtcText(nowUtc));
            insertAgent.Parameters.AddWithValue("@reportedVersion", reportedVersion);
            insertAgent.Parameters.AddWithValue("@reportedPlatform", reportedPlatform);
            insertAgent.Parameters.AddWithValue("@appliedRevision", 0L);
            await insertAgent.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await TryConsumeTokenAsync(connection, transaction, tokenHash, agentId, nowUtc, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return new AgentEnrollmentResult(agentId, nodeId.Value);
    }

    public async Task<AgentRecord?> GetAgentAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, node_id, enrolled_at_utc, last_seen_at_utc, reported_version,
                                     reported_platform, applied_revision, latest_metric_snapshot_json
                              FROM agents WHERE id = @id;
                              """;
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAgent(reader) : null;
    }

    public async Task<AgentAuthenticationRecord?> GetAgentAuthenticationAsync(Guid agentId,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, node_id, secret_hash FROM agents WHERE id = @id;";
        command.Parameters.AddWithValue("@id", agentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AgentAuthenticationRecord(
            SqliteValue.ToGuid(reader.GetString(0)),
            SqliteValue.ToGuid(reader.GetString(1)),
            reader.GetFieldValue<byte[]>(2));
    }

    public async Task<bool> TryUpdateAgentReportAsync(
        Guid agentId,
        string reportedVersion,
        string reportedPlatform,
        long appliedRevision,
        string? latestMetricSnapshotJson,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedPlatform);
        ArgumentOutOfRangeException.ThrowIfNegative(appliedRevision);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              UPDATE agents
                              SET last_seen_at_utc = @lastSeenAtUtc,
                                  reported_version = @reportedVersion,
                                  reported_platform = @reportedPlatform,
                                  applied_revision = @appliedRevision,
                                  latest_metric_snapshot_json = @latestMetricSnapshotJson
                              WHERE id = @id;
                              """;
        command.Parameters.AddWithValue("@id", agentId.ToString("D"));
        command.Parameters.AddWithValue("@lastSeenAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        command.Parameters.AddWithValue("@reportedVersion", reportedVersion);
        command.Parameters.AddWithValue("@reportedPlatform", reportedPlatform);
        command.Parameters.AddWithValue("@appliedRevision", appliedRevision);
        command.Parameters.AddWithValue("@latestMetricSnapshotJson", SqliteValue.ToDbValue(latestMetricSnapshotJson));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<NodeObservationRecord>> GetNodeObservationsAsync(
        CancellationToken cancellationToken)
    {
        var observations = new List<NodeObservationRecord>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT n.id, n.display_name, n.desired_revision,
                                     a.id, a.last_seen_at_utc, a.reported_version, a.reported_platform, a.applied_revision
                              FROM nodes n
                              LEFT JOIN agents a ON a.node_id = n.id
                              ORDER BY n.created_at_utc, n.id;
                              """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            observations.Add(new NodeObservationRecord(
                SqliteValue.ToGuid(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : SqliteValue.ToGuid(reader.GetString(3)),
                reader.IsDBNull(4) ? null : SqliteValue.ToDateTimeOffset(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        }

        return observations;
    }

    public async Task<AgentCommandRecord?> CreateRunHealthCheckCommandAsync(
        Guid commandId,
        Guid agentId,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        var createdAtUtc = timeProvider.GetUtcNow();
        if (expiresAtUtc <= createdAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Expiration must be in the future.");
        }

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              INSERT INTO agent_commands (
                                  id, agent_id, type, status, created_at_utc, started_at_utc, completed_at_utc, error_code, error_message, expires_at_utc)
                              SELECT @id, @agentId, 'RunHealthCheck', 'Pending', @createdAtUtc, NULL, NULL, NULL, NULL, @expiresAtUtc
                              WHERE EXISTS (SELECT 1 FROM agents WHERE id = @agentId);
                              """;
        command.Parameters.AddWithValue("@id", commandId.ToString("D"));
        command.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
        command.Parameters.AddWithValue("@createdAtUtc", SqliteValue.ToUtcText(createdAtUtc));
        command.Parameters.AddWithValue("@expiresAtUtc", SqliteValue.ToUtcText(expiresAtUtc));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            return null;
        }

        return new AgentCommandRecord(
            commandId,
            agentId,
            "RunHealthCheck",
            "Pending",
            createdAtUtc,
            null,
            null,
            null,
            null,
            expiresAtUtc);
    }

    public async Task<bool> TryUpdateAgentSyncAsync(
        Guid agentId,
        string reportedVersion,
        string reportedPlatform,
        long appliedRevision,
        string? latestMetricSnapshotJson,
        IReadOnlyList<ServiceRuntimeState> serviceStates,
        IReadOnlyList<AgentCommandResult> commandResults,
        CancellationToken cancellationToken,
        IReadOnlyList<UsageBatch>? usageBatches = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportedPlatform);
        ArgumentOutOfRangeException.ThrowIfNegative(appliedRevision);
        ArgumentNullException.ThrowIfNull(commandResults);
        ArgumentNullException.ThrowIfNull(serviceStates);

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var updateAgent = connection.CreateCommand())
            {
                updateAgent.Transaction = transaction;
                updateAgent.CommandText = """
                                          UPDATE agents
                                          SET last_seen_at_utc = @lastSeenAtUtc,
                                              reported_version = @reportedVersion,
                                              reported_platform = @reportedPlatform,
                                              applied_revision = @appliedRevision,
                                              latest_metric_snapshot_json = @latestMetricSnapshotJson
                                          WHERE id = @id;
                                          """;
                updateAgent.Parameters.AddWithValue("@id", agentId.ToString("D"));
                updateAgent.Parameters.AddWithValue("@lastSeenAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
                updateAgent.Parameters.AddWithValue("@reportedVersion", reportedVersion);
                updateAgent.Parameters.AddWithValue("@reportedPlatform", reportedPlatform);
                updateAgent.Parameters.AddWithValue("@appliedRevision", appliedRevision);
                updateAgent.Parameters.AddWithValue("@latestMetricSnapshotJson",
                    SqliteValue.ToDbValue(latestMetricSnapshotJson));
                if (await updateAgent.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            foreach (var result in commandResults)
            {
                await RecordCommandResultAsync(connection, transaction, agentId, result, cancellationToken);
            }

            foreach (var state in serviceStates)
            {
                await UpsertServiceRuntimeStateAsync(connection, transaction, agentId, state, cancellationToken);
            }

            if (usageBatches is not null)
            {
                foreach (var batch in usageBatches)
                {
                    if (!await RecordUsageBatchAsync(connection, transaction, agentId, batch, cancellationToken))
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return false;
                    }
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<long?> GetNodeDesiredRevisionForAgentAsync(Guid agentId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT n.desired_revision FROM agents a INNER JOIN nodes n ON n.id = a.node_id WHERE a.id = @agentId;";
        command.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long revision ? revision : null;
    }

    public async Task<IReadOnlyList<ServiceInstanceWithRuntimeRecord>> GetServicesForNodeAsync(Guid nodeId,
        CancellationToken cancellationToken)
    {
        var services = new List<ServiceInstanceWithRuntimeRecord>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT s.id, s.node_id, s.name, s.backend_type, s.backend_version, s.enabled, s.config_schema_version, s.config_json, s.created_at_utc, s.updated_at_utc,
                                     r.service_id, r.status, r.backend_version, r.applied_config_sha256, r.traffic_upload_bytes, r.traffic_download_bytes, r.traffic_observed_at_utc, r.observed_at_utc, r.error_code, r.error_message
                              FROM service_instances s
                              LEFT JOIN service_runtime_states r ON r.service_id = s.id
                              WHERE s.node_id = @nodeId
                              ORDER BY s.created_at_utc, s.id;
                              """;
        command.Parameters.AddWithValue("@nodeId", nodeId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var service = new ServiceInstanceRecord(SqliteValue.ToGuid(reader.GetString(0)),
                SqliteValue.ToGuid(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5) != 0, reader.GetInt32(6), reader.GetString(7),
                SqliteValue.ToDateTimeOffset(reader.GetString(8)), SqliteValue.ToDateTimeOffset(reader.GetString(9)));
            ServiceRuntimeStateRecord? runtime = reader.IsDBNull(10)
                ? null
                : new ServiceRuntimeStateRecord(SqliteValue.ToGuid(reader.GetString(10)), reader.GetInt32(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13), reader.IsDBNull(14) ? null : reader.GetInt64(14),
                    reader.IsDBNull(15) ? null : reader.GetInt64(15),
                    reader.IsDBNull(16) ? null : SqliteValue.ToDateTimeOffset(reader.GetString(16)),
                    SqliteValue.ToDateTimeOffset(reader.GetString(17)),
                    reader.IsDBNull(18) ? null : reader.GetString(18),
                    reader.IsDBNull(19) ? null : reader.GetString(19));
            services.Add(new ServiceInstanceWithRuntimeRecord(service, runtime));
        }

        return services;
    }

    public async Task<IReadOnlyList<ServiceTemplateRecord>> GetServiceTemplatesAsync(
        CancellationToken cancellationToken)
    {
        var templates = new List<ServiceTemplateRecord>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, normalized_name, backend_type, backend_version, config_schema_version, config_json, created_at_utc, updated_at_utc FROM service_templates ORDER BY normalized_name, id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) templates.Add(ReadServiceTemplate(reader));
        return templates;
    }

    public async Task<ServiceTemplateRecord?> GetServiceTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, normalized_name, backend_type, backend_version, config_schema_version, config_json, created_at_utc, updated_at_utc FROM service_templates WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadServiceTemplate(reader) : null;
    }

    public async Task<bool> CreateServiceTemplateAsync(ServiceTemplateRecord template,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO service_templates (id, name, normalized_name, backend_type, backend_version, config_schema_version, config_json, created_at_utc, updated_at_utc) VALUES (@id,@name,@normalizedName,@backendType,@backendVersion,@schema,@config,@created,@updated);";
        AddTemplateParameters(command, template);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task<bool?> UpdateServiceTemplateAsync(ServiceTemplateRecord template,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM service_templates WHERE id = @id;";
        exists.Parameters.AddWithValue("@id", template.Id.ToString("D"));
        if (await exists.ExecuteScalarAsync(cancellationToken) is null) return null;
        await using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE service_templates SET name=@name, normalized_name=@normalizedName, backend_type=@backendType, backend_version=@backendVersion, config_schema_version=@schema, config_json=@config, updated_at_utc=@updated WHERE id=@id;";
        AddTemplateParameters(command, template);
        try
        {
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public async Task<bool> DeleteServiceTemplateAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM service_templates WHERE id = @id;";
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<(ServiceInstanceRecord? Service, long Revision)> CreateServiceFromTemplateAsync(Guid nodeId,
        Guid templateId, string name, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = timeProvider.GetUtcNow();
            var service = new ServiceInstanceRecord(Guid.NewGuid(), nodeId, name, string.Empty, string.Empty, true, 0,
                string.Empty, now, now);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                                 INSERT INTO service_instances (id,node_id,name,backend_type,backend_version,enabled,config_schema_version,config_json,created_at_utc,updated_at_utc)
                                 SELECT @id,@nodeId,@name,backend_type,backend_version,1,config_schema_version,config_json,@createdAtUtc,@updatedAtUtc
                                 FROM service_templates WHERE id=@templateId AND EXISTS (SELECT 1 FROM nodes WHERE id=@nodeId);
                                 """;
            insert.Parameters.AddWithValue("@templateId", templateId.ToString("D"));
            AddServiceParameters(insert, service);
            if (await insert.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (null, 0);
            }

            await using var select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText =
                "SELECT backend_type,backend_version,config_schema_version,config_json FROM service_instances WHERE id=@id;";
            select.Parameters.AddWithValue("@id", service.Id.ToString("D"));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            service = service with
            {
                BackendType = reader.GetString(0), BackendVersion = reader.GetString(1),
                ConfigSchemaVersion = reader.GetInt32(2), ConfigJson = reader.GetString(3)
            };
            var revision = await IncrementRevisionAsync(connection, transaction, nodeId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (service, revision);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<BatchServiceEnabledNodeResult>?> SetServicesEnabledBatchAsync(
        IReadOnlyList<BatchServiceEnabledItemRecord> items, CancellationToken cancellationToken)
    {
        if (items.Count is < 1 or > 256 ||
            items.Select(static item => item.ServiceId).Distinct().Count() != items.Count)
            return null;

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in items)
            {
                await using var valid = connection.CreateCommand();
                valid.Transaction = transaction;
                valid.CommandText = "SELECT 1 FROM service_instances WHERE id=@serviceId AND node_id=@nodeId;";
                valid.Parameters.AddWithValue("@serviceId", item.ServiceId.ToString("D"));
                valid.Parameters.AddWithValue("@nodeId", item.NodeId.ToString("D"));
                if (await valid.ExecuteScalarAsync(cancellationToken) is null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
            }

            var now = SqliteValue.ToUtcText(timeProvider.GetUtcNow());
            foreach (var item in items)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE service_instances SET enabled=@enabled,updated_at_utc=@now WHERE id=@serviceId AND node_id=@nodeId;";
                update.Parameters.AddWithValue("@enabled", item.Enabled ? 1 : 0);
                update.Parameters.AddWithValue("@now", now);
                update.Parameters.AddWithValue("@serviceId", item.ServiceId.ToString("D"));
                update.Parameters.AddWithValue("@nodeId", item.NodeId.ToString("D"));
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            var results = new List<BatchServiceEnabledNodeResult>();
            foreach (var nodeId in items.Select(static item => item.NodeId).Distinct().Order())
                results.Add(new BatchServiceEnabledNodeResult(nodeId,
                    await IncrementRevisionAsync(connection, transaction, nodeId, cancellationToken)));
            await transaction.CommitAsync(cancellationToken);
            return results;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<HealthServiceRecord>> GetHealthServicesAsync(CancellationToken cancellationToken)
    {
        var services = new List<HealthServiceRecord>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT s.id,s.node_id,s.name,r.status,r.error_code,r.error_message FROM service_instances s LEFT JOIN service_runtime_states r ON r.service_id=s.id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            services.Add(new HealthServiceRecord(SqliteValue.ToGuid(reader.GetString(0)),
                SqliteValue.ToGuid(reader.GetString(1)), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        return services;
    }

    public async Task<(ServiceInstanceRecord? Service, long Revision)> CreateServiceAsync(ServiceInstanceRecord service,
        CancellationToken cancellationToken) =>
        await MutateServiceAsync(service.NodeId, async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                                  INSERT INTO service_instances (id, node_id, name, backend_type, backend_version, enabled, config_schema_version, config_json, created_at_utc, updated_at_utc)
                                  SELECT @id, @nodeId, @name, @backendType, @backendVersion, @enabled, @configSchemaVersion, @configJson, @createdAtUtc, @updatedAtUtc
                                  WHERE EXISTS (SELECT 1 FROM nodes WHERE id = @nodeId);
                                  """;
            AddServiceParameters(command, service);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, service, cancellationToken);

    public async Task<(ServiceInstanceRecord? Service, long Revision)> UpdateServiceAsync(ServiceInstanceRecord service,
        CancellationToken cancellationToken) =>
        await MutateServiceAsync(service.NodeId, async (connection, transaction) =>
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                                  UPDATE service_instances
                                  SET name = @name, backend_version = @backendVersion, enabled = @enabled, config_schema_version = @configSchemaVersion, config_json = @configJson, updated_at_utc = @updatedAtUtc
                                  WHERE id = @id AND node_id = @nodeId;
                                  """;
            AddServiceParameters(command, service);
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, service, cancellationToken);

    public async Task<long?> DeleteServiceAsync(Guid nodeId, Guid serviceId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var table in new[]
                     {
                         "usage_totals", "user_service_bindings", "service_public_endpoints",
                         "service_runtime_states"
                     })
            {
                await using var child = connection.CreateCommand();
                child.Transaction = transaction;
                child.CommandText = $"DELETE FROM {table} WHERE service_id = @id;";
                child.Parameters.AddWithValue("@id", serviceId.ToString("D"));
                await child.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM service_instances WHERE id = @id AND node_id = @nodeId;";
            command.Parameters.AddWithValue("@id", serviceId.ToString("D"));
            command.Parameters.AddWithValue("@nodeId", nodeId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var revision = await IncrementRevisionAsync(connection, transaction, nodeId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return revision;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<long?> SetServiceEnabledAsync(Guid nodeId, Guid serviceId, bool enabled,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE service_instances SET enabled = @enabled, updated_at_utc = @updatedAtUtc WHERE id = @id AND node_id = @nodeId;";
            command.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
            command.Parameters.AddWithValue("@updatedAtUtc", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            command.Parameters.AddWithValue("@id", serviceId.ToString("D"));
            command.Parameters.AddWithValue("@nodeId", nodeId.ToString("D"));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var revision = await IncrementRevisionAsync(connection, transaction, nodeId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return revision;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<(long Revision, IReadOnlyList<ServiceInstanceRecord> Services)?> GetDesiredStateForAgentAsync(
        Guid agentId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT n.desired_revision, s.id, s.node_id, s.name, s.backend_type, s.backend_version, s.enabled, s.config_schema_version, s.config_json, s.created_at_utc, s.updated_at_utc
                              FROM agents a INNER JOIN nodes n ON n.id = a.node_id LEFT JOIN service_instances s ON s.node_id = n.id
                              WHERE a.id = @agentId ORDER BY s.created_at_utc, s.id;
                              """;
        command.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var revision = reader.GetInt64(0);
        var services = new List<ServiceInstanceRecord>();
        do
        {
            if (!reader.IsDBNull(1))
                services.Add(new ServiceInstanceRecord(SqliteValue.ToGuid(reader.GetString(1)),
                    SqliteValue.ToGuid(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetInt64(6) != 0, reader.GetInt32(7), reader.GetString(8),
                    SqliteValue.ToDateTimeOffset(reader.GetString(9)),
                    SqliteValue.ToDateTimeOffset(reader.GetString(10))));
        } while (await reader.ReadAsync(cancellationToken));

        return (revision, services);
    }

    public async Task<IReadOnlyList<AgentCommandRecord>> GetActiveHealthCheckCommandsAsync(Guid agentId,
        CancellationToken cancellationToken)
    {
        var nowUtc = timeProvider.GetUtcNow();
        var commands = new List<AgentCommandRecord>();
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, agent_id, type, status, created_at_utc, started_at_utc, completed_at_utc, error_code, error_message, expires_at_utc
                              FROM agent_commands
                              WHERE agent_id = @agentId
                                AND type = 'RunHealthCheck'
                                AND status IN ('Pending', 'Running')
                                AND (expires_at_utc IS NULL OR expires_at_utc > @nowUtc)
                              ORDER BY created_at_utc;
                              """;
        command.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
        command.Parameters.AddWithValue("@nowUtc", SqliteValue.ToUtcText(nowUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            commands.Add(ReadAgentCommand(reader));
        }

        return commands;
    }

    public async Task<AgentCommandRecord?> GetAgentCommandAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
                              SELECT id, agent_id, type, status, created_at_utc, started_at_utc, completed_at_utc, error_code, error_message, expires_at_utc
                              FROM agent_commands WHERE id = @id;
                              """;
        command.Parameters.AddWithValue("@id", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAgentCommand(reader) : null;
    }

    private static async Task RecordCommandResultAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid agentId,
        AgentCommandResult result,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = result.Status == AgentCommandStatus.Running
            ? """
              UPDATE agent_commands
              SET status = 'Running',
                  started_at_utc = COALESCE(started_at_utc, @startedAtUtc)
              WHERE id = @id
                AND agent_id = @agentId
                AND status IN ('Pending', 'Running');
              """
            : """
              UPDATE agent_commands
              SET status = @status,
                  started_at_utc = COALESCE(started_at_utc, @startedAtUtc),
                  completed_at_utc = @completedAtUtc,
                  error_code = @errorCode,
                  error_message = @errorMessage
              WHERE id = @id
                AND agent_id = @agentId
                AND status NOT IN ('Succeeded', 'Failed');
              """;
        command.Parameters.AddWithValue("@id", result.CommandId.ToString("D"));
        command.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
        command.Parameters.AddWithValue("@status", result.Status.ToString());
        command.Parameters.AddWithValue("@startedAtUtc", SqliteValue.ToUtcText(result.StartedAt));
        command.Parameters.AddWithValue("@completedAtUtc", SqliteValue.ToDbValue(result.CompletedAt));
        command.Parameters.AddWithValue("@errorCode", SqliteValue.ToDbValue(result.ErrorCode));
        command.Parameters.AddWithValue("@errorMessage", SqliteValue.ToDbValue(result.ErrorMessage));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertServiceRuntimeStateAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid agentId, ServiceRuntimeState state, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              INSERT INTO service_runtime_states (service_id, status, backend_version, applied_config_sha256, traffic_upload_bytes, traffic_download_bytes, traffic_observed_at_utc, observed_at_utc, error_code, error_message)
                              SELECT @serviceId, @status, @backendVersion, @appliedConfigSha256, @trafficUploadBytes, @trafficDownloadBytes, @trafficObservedAtUtc, @observedAtUtc, @errorCode, @errorMessage
                              WHERE EXISTS (SELECT 1 FROM service_instances s INNER JOIN agents a ON a.node_id = s.node_id WHERE s.id = @serviceId AND a.id = @agentId)
                              ON CONFLICT(service_id) DO UPDATE SET status = excluded.status, backend_version = excluded.backend_version, applied_config_sha256 = excluded.applied_config_sha256, traffic_upload_bytes = excluded.traffic_upload_bytes, traffic_download_bytes = excluded.traffic_download_bytes, traffic_observed_at_utc = excluded.traffic_observed_at_utc, observed_at_utc = excluded.observed_at_utc, error_code = excluded.error_code, error_message = excluded.error_message;
                              """;
        command.Parameters.AddWithValue("@serviceId", state.ServiceId.ToString("D"));
        command.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
        command.Parameters.AddWithValue("@status", (int)state.Status);
        command.Parameters.AddWithValue("@backendVersion", SqliteValue.ToDbValue(state.BackendVersion));
        command.Parameters.AddWithValue("@appliedConfigSha256", SqliteValue.ToDbValue(state.AppliedConfigSha256));
        command.Parameters.AddWithValue("@trafficUploadBytes",
            state.Traffic is null ? DBNull.Value : state.Traffic.UploadBytes);
        command.Parameters.AddWithValue("@trafficDownloadBytes",
            state.Traffic is null ? DBNull.Value : state.Traffic.DownloadBytes);
        command.Parameters.AddWithValue("@trafficObservedAtUtc",
            state.Traffic is null ? DBNull.Value : SqliteValue.ToUtcText(state.Traffic.ObservedAt));
        command.Parameters.AddWithValue("@observedAtUtc", SqliteValue.ToUtcText(state.ObservedAt));
        command.Parameters.AddWithValue("@errorCode", SqliteValue.ToDbValue(state.ErrorCode));
        command.Parameters.AddWithValue("@errorMessage", SqliteValue.ToDbValue(state.ErrorMessage));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> RecordUsageBatchAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid agentId, UsageBatch batch, CancellationToken cancellationToken)
    {
        // Validate the entire batch before recording its idempotency receipt.
        foreach (var record in batch.Records)
        {
            await using var valid = connection.CreateCommand();
            valid.Transaction = transaction;
            valid.CommandText = """
                                SELECT 1 FROM service_instances s
                                INNER JOIN agents a ON a.node_id = s.node_id
                                INNER JOIN user_service_bindings b ON b.service_id = s.id
                                WHERE a.id = @agentId AND s.id = @serviceId AND b.user_id = @userId;
                                """;
            valid.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
            valid.Parameters.AddWithValue("@serviceId", record.ServiceId.ToString("D"));
            valid.Parameters.AddWithValue("@userId", record.UserId.ToString("D"));
            if (await valid.ExecuteScalarAsync(cancellationToken) is null) return false;
        }

        await using var receipt = connection.CreateCommand();
        receipt.Transaction = transaction;
        receipt.CommandText =
            "INSERT INTO usage_batches (agent_id,batch_id,observed_at_utc,accepted_at_utc) VALUES (@agent,@batch,@observed,@accepted) ON CONFLICT(agent_id,batch_id) DO NOTHING;";
        receipt.Parameters.AddWithValue("@agent", agentId.ToString("D"));
        receipt.Parameters.AddWithValue("@batch", batch.BatchId.ToString("D"));
        receipt.Parameters.AddWithValue("@observed", SqliteValue.ToUtcText(batch.ObservedAt));
        receipt.Parameters.AddWithValue("@accepted", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        if (await receipt.ExecuteNonQueryAsync(cancellationToken) == 0) return true;

        foreach (var record in batch.Records)
        {
            await using var total = connection.CreateCommand();
            total.Transaction = transaction;
            total.CommandText = """
                                INSERT INTO usage_totals (user_id,service_id,upload_bytes,download_bytes,updated_at_utc)
                                VALUES (@user,@service,@upload,@download,@now)
                                ON CONFLICT(user_id,service_id) DO UPDATE SET
                                    upload_bytes = usage_totals.upload_bytes + excluded.upload_bytes,
                                    download_bytes = usage_totals.download_bytes + excluded.download_bytes,
                                    updated_at_utc = excluded.updated_at_utc
                                WHERE usage_totals.upload_bytes <= @maximum - excluded.upload_bytes
                                  AND usage_totals.download_bytes <= @maximum - excluded.download_bytes;
                                """;
            total.Parameters.AddWithValue("@user", record.UserId.ToString("D"));
            total.Parameters.AddWithValue("@service", record.ServiceId.ToString("D"));
            total.Parameters.AddWithValue("@upload", record.UploadBytes);
            total.Parameters.AddWithValue("@download", record.DownloadBytes);
            total.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            total.Parameters.AddWithValue("@maximum", long.MaxValue);
            if (await total.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }

        return true;
    }

    private async Task<(ServiceInstanceRecord? Service, long Revision)> MutateServiceAsync(Guid nodeId,
        Func<SqliteConnection, SqliteTransaction, Task<bool>> operation, ServiceInstanceRecord service,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            if (!await operation(connection, transaction))
            {
                await transaction.RollbackAsync(cancellationToken);
                return (null, 0);
            }

            var revision = await IncrementRevisionAsync(connection, transaction, nodeId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (service, revision);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void AddServiceParameters(SqliteCommand command, ServiceInstanceRecord service)
    {
        command.Parameters.AddWithValue("@id", service.Id.ToString("D"));
        command.Parameters.AddWithValue("@nodeId", service.NodeId.ToString("D"));
        command.Parameters.AddWithValue("@name", service.Name);
        command.Parameters.AddWithValue("@backendType", service.BackendType);
        command.Parameters.AddWithValue("@backendVersion", service.BackendVersion);
        command.Parameters.AddWithValue("@enabled", service.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@configSchemaVersion", service.ConfigSchemaVersion);
        command.Parameters.AddWithValue("@configJson", service.ConfigJson);
        command.Parameters.AddWithValue("@createdAtUtc", SqliteValue.ToUtcText(service.CreatedAtUtc));
        command.Parameters.AddWithValue("@updatedAtUtc", SqliteValue.ToUtcText(service.UpdatedAtUtc));
    }

    private static void AddTemplateParameters(SqliteCommand command, ServiceTemplateRecord template)
    {
        command.Parameters.AddWithValue("@id", template.Id.ToString("D"));
        command.Parameters.AddWithValue("@name", template.Name);
        command.Parameters.AddWithValue("@normalizedName", template.NormalizedName);
        command.Parameters.AddWithValue("@backendType", template.BackendType);
        command.Parameters.AddWithValue("@backendVersion", template.BackendVersion);
        command.Parameters.AddWithValue("@schema", template.ConfigSchemaVersion);
        command.Parameters.AddWithValue("@config", template.ConfigJson);
        command.Parameters.AddWithValue("@created", SqliteValue.ToUtcText(template.CreatedAtUtc));
        command.Parameters.AddWithValue("@updated", SqliteValue.ToUtcText(template.UpdatedAtUtc));
    }

    private static async Task<long> IncrementRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid nodeId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "UPDATE nodes SET desired_revision = desired_revision + 1 WHERE id = @nodeId RETURNING desired_revision;";
        command.Parameters.AddWithValue("@nodeId", nodeId.ToString("D"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ??
                      throw new InvalidOperationException("Node does not exist."));
    }

    private static async Task<Guid?> GetEligibleTokenNodeIdAsync(SqliteConnection connection,
        SqliteTransaction transaction, byte[] tokenHash, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              SELECT node_id
                              FROM enrollment_tokens
                              WHERE token_hash = @tokenHash
                                AND consumed_at_utc IS NULL
                                AND expires_at_utc > @nowUtc;
                              """;
        command.Parameters.Add("@tokenHash", SqliteType.Blob).Value = tokenHash;
        command.Parameters.AddWithValue("@nowUtc", SqliteValue.ToUtcText(nowUtc));
        return await command.ExecuteScalarAsync(cancellationToken) is string nodeId ? SqliteValue.ToGuid(nodeId) : null;
    }

    private static async Task<bool> TryConsumeTokenAsync(SqliteConnection connection, SqliteTransaction transaction,
        byte[] tokenHash, Guid agentId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
                              UPDATE enrollment_tokens
                              SET consumed_at_utc = @consumedAtUtc,
                                  consumed_by_agent_id = @agentId
                              WHERE token_hash = @tokenHash
                                AND consumed_at_utc IS NULL
                                AND expires_at_utc > @nowUtc;
                              """;
        command.Parameters.AddWithValue("@consumedAtUtc", SqliteValue.ToUtcText(nowUtc));
        command.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
        command.Parameters.Add("@tokenHash", SqliteType.Blob).Value = tokenHash;
        command.Parameters.AddWithValue("@nowUtc", SqliteValue.ToUtcText(nowUtc));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static NodeRecord ReadNode(SqliteDataReader reader) => new(
        SqliteValue.ToGuid(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2),
        SqliteValue.ToDateTimeOffset(reader.GetString(3)));

    private static ServiceTemplateRecord ReadServiceTemplate(SqliteDataReader reader) => new(
        SqliteValue.ToGuid(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetInt32(5), reader.GetString(6), SqliteValue.ToDateTimeOffset(reader.GetString(7)),
        SqliteValue.ToDateTimeOffset(reader.GetString(8)));

    private static EnrollmentTokenRecord ReadEnrollmentToken(SqliteDataReader reader) => new(
        SqliteValue.ToGuid(reader.GetString(0)), SqliteValue.ToGuid(reader.GetString(1)),
        SqliteValue.ToDateTimeOffset(reader.GetString(2)), SqliteValue.ToDateTimeOffset(reader.GetString(3)),
        reader.IsDBNull(4) ? null : SqliteValue.ToDateTimeOffset(reader.GetString(4)),
        reader.IsDBNull(5) ? null : SqliteValue.ToGuid(reader.GetString(5)));

    private static AgentRecord ReadAgent(SqliteDataReader reader) => new(
        SqliteValue.ToGuid(reader.GetString(0)), SqliteValue.ToGuid(reader.GetString(1)),
        SqliteValue.ToDateTimeOffset(reader.GetString(2)),
        reader.IsDBNull(3) ? null : SqliteValue.ToDateTimeOffset(reader.GetString(3)),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7));

    private static AgentCommandRecord ReadAgentCommand(SqliteDataReader reader) => new(
        SqliteValue.ToGuid(reader.GetString(0)), SqliteValue.ToGuid(reader.GetString(1)), reader.GetString(2),
        reader.GetString(3),
        SqliteValue.ToDateTimeOffset(reader.GetString(4)),
        reader.IsDBNull(5) ? null : SqliteValue.ToDateTimeOffset(reader.GetString(5)),
        reader.IsDBNull(6) ? null : SqliteValue.ToDateTimeOffset(reader.GetString(6)),
        reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : SqliteValue.ToDateTimeOffset(reader.GetString(9)));
}