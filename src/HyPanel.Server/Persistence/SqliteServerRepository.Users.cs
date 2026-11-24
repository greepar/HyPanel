using System.Security.Cryptography;
using System.Text;
using HyPanel.Server.Backends;
using HyPanel.Server.Security;
using HyPanel.Shared.Networking;
using HyPanel.Shared.Serialization;
using Microsoft.Data.Sqlite;

namespace HyPanel.Server.Persistence;

internal sealed partial class SqliteServerRepository
{
    public async Task<UserIssue> CreateUserAsync(Guid id, string username, string normalizedUsername,
        string passwordHash, string role, bool enabled, long? trafficLimitBytes, DateTimeOffset? expiresAtUtc,
        string subscriptionToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = TokenHash(subscriptionToken);
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO users (id,username,normalized_username,password_hash,role,enabled,traffic_limit_bytes,expires_at_utc,subscription_token_hash,created_at_utc,updated_at_utc) VALUES (@id,@username,@normalized,@password,@role,@enabled,@limit,@expires,@token,@now,@now);";
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@normalized", normalizedUsername);
        command.Parameters.AddWithValue("@password", passwordHash);
        command.Parameters.AddWithValue("@role", role);
        command.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("@limit", trafficLimitBytes is null ? DBNull.Value : trafficLimitBytes.Value);
        command.Parameters.AddWithValue("@expires",
            expiresAtUtc is null ? DBNull.Value : SqliteValue.ToUtcText(expiresAtUtc.Value));
        command.Parameters.Add("@token", SqliteType.Blob).Value = hash;
        command.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await StoreRecoverableTokenAsync(connection, id, subscriptionToken, cancellationToken);
        return new UserIssue(
            new UserRecord(id, username, normalizedUsername, role, enabled, trafficLimitBytes, expiresAtUtc, now, now),
            subscriptionToken);
    }

    public async Task<UserRecord?> GetUserByNormalizedUsernameAsync(string normalizedUsername,
        CancellationToken cancellationToken)
    {
        await using var c = await connectionFactory.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id,username,normalized_username,role,enabled,traffic_limit_bytes,expires_at_utc,created_at_utc,updated_at_utc,traffic_reset_day FROM users WHERE normalized_username=@name;";
        cmd.Parameters.AddWithValue("@name", normalizedUsername);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        return await r.ReadAsync(cancellationToken) ? ReadUser(r) : null;
    }

    public async Task<(UserRecord? User, string? PasswordHash)> GetLoginUserAsync(string normalizedUsername,
        CancellationToken cancellationToken)
    {
        await using var c = await connectionFactory.OpenAsync(cancellationToken);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id,username,normalized_username,password_hash,role,enabled,traffic_limit_bytes,expires_at_utc,created_at_utc,updated_at_utc,traffic_reset_day FROM users WHERE normalized_username=@name;";
        cmd.Parameters.AddWithValue("@name", normalizedUsername);
        await using var r = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await r.ReadAsync(cancellationToken)) return (null, null);
        return (
            new UserRecord(SqliteValue.ToGuid(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(4),
                r.GetInt64(5) != 0, r.IsDBNull(6) ? null : r.GetInt64(6),
                r.IsDBNull(7) ? null : SqliteValue.ToDateTimeOffset(r.GetString(7)),
                SqliteValue.ToDateTimeOffset(r.GetString(8)), SqliteValue.ToDateTimeOffset(r.GetString(9))),
            r.GetString(3));
    }

    public async Task<SessionIssue> CreateSessionAsync(UserRecord user, string token, DateTimeOffset expiresAtUtc,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO user_sessions (id,user_id,token_hash,created_at_utc,expires_at_utc,revoked_at_utc) VALUES (@id,@user,@hash,@now,@expires,NULL);";
        cmd.Parameters.AddWithValue("@id", Guid.NewGuid().ToString("D"));
        cmd.Parameters.AddWithValue("@user", user.Id.ToString("D"));
        cmd.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        cmd.Parameters.AddWithValue("@expires", SqliteValue.ToUtcText(expiresAtUtc));
        await cmd.ExecuteNonQueryAsync(ct);
        return new SessionIssue(user, token, expiresAtUtc);
    }

    public async Task<UserRecord?> AuthenticateSessionAsync(string token, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT u.id,u.username,u.normalized_username,u.role,u.enabled,u.traffic_limit_bytes,u.expires_at_utc,u.created_at_utc,u.updated_at_utc,u.traffic_reset_day FROM user_sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@hash AND s.revoked_at_utc IS NULL AND s.expires_at_utc>@now AND u.enabled=1 AND (u.expires_at_utc IS NULL OR u.expires_at_utc>@now);";
        cmd.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadUser(r) : null;
    }

    public async Task RevokeSessionAsync(string token, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE user_sessions SET revoked_at_utc=@now WHERE token_hash=@hash AND revoked_at_utc IS NULL;";
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        cmd.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Issues a new subscription token and rotates every proxy credential of the user, so a leaked link or
    /// already-imported profile stops working after the next Agent sync.
    /// </summary>
    public async Task<string?> ResetSubscriptionAsync(Guid id, string token, CancellationToken ct)
    {
        var rotated = await RotateSubscriptionTokenAsync(id, token, ct);
        if (rotated is null) return null;
        foreach (var serviceId in await GetBoundServicesAsync(id, ct))
            await RotateServiceCredentialAsync(id, serviceId, ct);
        return rotated;
    }

    public async Task<string?> RotateSubscriptionTokenAsync(Guid id, string token, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET subscription_token_hash=@hash,updated_at_utc=@now WHERE id=@id;";
        cmd.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) return null;
        await StoreRecoverableTokenAsync(c, id, token, ct);
        return token;
    }

    /// <summary>Returns the current subscription token, or null when it was issued before tokens became recoverable.</summary>
    public async Task<string?> GetSubscriptionTokenAsync(Guid id, CancellationToken ct)
    {
        if (!credentialProtector.IsConfigured) return null;
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT subscription_token_nonce,subscription_token_ciphertext,subscription_token_tag FROM users WHERE id=@id AND subscription_token_nonce IS NOT NULL;";
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return credentialProtector.UnprotectSubscriptionToken(id,
            new ProtectedCredential((byte[])r[0], (byte[])r[1], (byte[])r[2]));
    }

    private async Task StoreRecoverableTokenAsync(SqliteConnection c, Guid id, string token, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        if (credentialProtector.IsConfigured)
        {
            var value = credentialProtector.ProtectSubscriptionToken(id, token);
            cmd.CommandText = "UPDATE users SET subscription_token_nonce=@nonce,subscription_token_ciphertext=@cipher,subscription_token_tag=@tag WHERE id=@id;";
            cmd.Parameters.Add("@nonce", SqliteType.Blob).Value = value.Nonce;
            cmd.Parameters.Add("@cipher", SqliteType.Blob).Value = value.Ciphertext;
            cmd.Parameters.Add("@tag", SqliteType.Blob).Value = value.Tag;
        }
        else
        {
            cmd.CommandText = "UPDATE users SET subscription_token_nonce=NULL,subscription_token_ciphertext=NULL,subscription_token_tag=NULL WHERE id=@id;";
        }
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<UserRecord>> GetUsersAsync(CancellationToken ct)
    {
        var users = new List<UserRecord>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id,username,normalized_username,role,enabled,traffic_limit_bytes,expires_at_utc,created_at_utc,updated_at_utc,traffic_reset_day FROM users ORDER BY created_at_utc,id;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) users.Add(ReadUser(r));
        return users;
    }

    public async Task<UserRecord?> GetUserAsync(Guid id, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id,username,normalized_username,role,enabled,traffic_limit_bytes,expires_at_utc,created_at_utc,updated_at_utc,traffic_reset_day FROM users WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadUser(r) : null;
    }

    public async Task<UserRecord?> UpdateUserAsync(Guid id, string username, string normalizedUsername,
        string? passwordHash, string role, bool enabled, long? trafficLimitBytes, DateTimeOffset? expiresAtUtc,
        CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "UPDATE users SET username=@username,normalized_username=@normalized,password_hash=COALESCE(@password,password_hash),role=@role,enabled=@enabled,traffic_limit_bytes=@limit,expires_at_utc=@expires,updated_at_utc=@now WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        cmd.Parameters.AddWithValue("@username", username);
        cmd.Parameters.AddWithValue("@normalized", normalizedUsername);
        cmd.Parameters.AddWithValue("@password", passwordHash is null ? DBNull.Value : passwordHash);
        cmd.Parameters.AddWithValue("@role", role);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@limit", trafficLimitBytes is null ? DBNull.Value : trafficLimitBytes.Value);
        cmd.Parameters.AddWithValue("@expires",
            expiresAtUtc is null ? DBNull.Value : SqliteValue.ToUtcText(expiresAtUtc.Value));
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        await IncrementBoundNodeRevisionsAsync(c, tx, id, ct);
        await tx.CommitAsync(ct);
        return await GetUserAsync(id, ct);
    }

    public async Task<bool> DeleteUserAsync(Guid id, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await IncrementBoundNodeRevisionsAsync(c, tx, id, ct);
            foreach (var table in new[] { "usage_totals", "usage_previous_period", "user_service_credentials", "user_service_bindings", "user_sessions", "user_passkeys" })
            {
                await using var child = c.CreateCommand();
                child.Transaction = tx;
                child.CommandText = $"DELETE FROM {table} WHERE user_id=@id;";
                child.Parameters.AddWithValue("@id", id.ToString("D"));
                await child.ExecuteNonQueryAsync(ct);
            }

            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM users WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            var deleted = await cmd.ExecuteNonQueryAsync(ct) == 1;
            if (!deleted)
            {
                await tx.RollbackAsync(ct);
                return false;
            }

            await tx.CommitAsync(ct);
            return true;
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> BindServiceAsync(Guid userId, Guid serviceId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        var backend = await GetBindingBackendAsync(c, tx, userId, serviceId, requireBinding: false, ct);
        if (!BackendCapabilities.IsMultiUser(backend))
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        var now = timeProvider.GetUtcNow();
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO user_service_bindings (user_id,service_id,created_at_utc) VALUES (@user,@service,@now) ON CONFLICT(user_id,service_id) DO NOTHING;";
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        var inserted = await cmd.ExecuteNonQueryAsync(ct) == 1;
        if (inserted)
        {
            await UpsertCredentialAsync(c, tx, userId, serviceId, backend, Guid.NewGuid().ToString("D"), now, ct);
            await IncrementServiceNodeRevisionAsync(c, tx, serviceId, ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    private async Task<List<Guid>> ReadGuidsAsync(string sql, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var ids = new List<Guid>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(SqliteValue.ToGuid(reader.GetString(0)));
        return ids;
    }

    public async Task InitializeProxyCredentialsAsync(CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using (var existing = c.CreateCommand())
        {
            existing.Transaction = tx;
            existing.CommandText = "SELECT EXISTS(SELECT 1 FROM user_service_credentials);";
            if ((long)(await existing.ExecuteScalarAsync(ct) ?? 0L) != 0 && !credentialProtector.IsConfigured)
                throw new InvalidOperationException(
                    "HyPanel:Security:MasterKey is required because encrypted proxy credentials already exist.");
        }
        var missing = new List<(Guid UserId, Guid ServiceId, Guid NodeId, string Backend)>();
        await using (var select = c.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText = $"""
                SELECT b.user_id,b.service_id,s.node_id,s.backend_type
                FROM user_service_bindings b
                JOIN service_instances s ON s.id=b.service_id
                LEFT JOIN user_service_credentials k ON k.user_id=b.user_id AND k.service_id=b.service_id
                WHERE k.user_id IS NULL AND s.backend_type IN {BackendCapabilities.MultiUserSqlList}
                ORDER BY b.user_id,b.service_id;
                """;
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                missing.Add((SqliteValue.ToGuid(reader.GetString(0)), SqliteValue.ToGuid(reader.GetString(1)),
                    SqliteValue.ToGuid(reader.GetString(2)), reader.GetString(3)));
        }
        if (missing.Count == 0)
        {
            await tx.CommitAsync(ct);
            return;
        }
        var now = timeProvider.GetUtcNow();
        foreach (var item in missing)
            await UpsertCredentialAsync(c, tx, item.UserId, item.ServiceId, item.Backend,
                Guid.NewGuid().ToString("D"), now, ct);
        foreach (var nodeId in missing.Select(item => item.NodeId).Distinct())
        {
            await using var revision = c.CreateCommand();
            revision.Transaction = tx;
            revision.CommandText = "UPDATE nodes SET desired_revision=desired_revision+1 WHERE id=@node;";
            revision.Parameters.AddWithValue("@node", nodeId.ToString("D"));
            await revision.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<bool> UnbindServiceAsync(Guid userId, Guid serviceId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using (var credential = c.CreateCommand())
        {
            credential.Transaction = tx;
            credential.CommandText = "UPDATE user_service_credentials SET status='Revoked',desired_eligible=0,updated_at_utc=@now WHERE user_id=@user AND service_id=@service;";
            credential.Parameters.AddWithValue("@user", userId.ToString("D"));
            credential.Parameters.AddWithValue("@service", serviceId.ToString("D"));
            credential.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
            await credential.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM user_service_bindings WHERE user_id=@user AND service_id=@service;";
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        var deleted = await cmd.ExecuteNonQueryAsync(ct) == 1;
        if (deleted) await IncrementServiceNodeRevisionAsync(c, tx, serviceId, ct);
        await tx.CommitAsync(ct);
        return deleted;
    }

    public async Task<UserServiceCredentialRecord?> RotateServiceCredentialAsync(Guid userId, Guid serviceId,
        CancellationToken ct) => await ReplaceServiceCredentialAsync(userId, serviceId, "Active", ct);

    public async Task<UserServiceCredentialRecord?> RevokeServiceCredentialAsync(Guid userId, Guid serviceId,
        CancellationToken ct) => await ReplaceServiceCredentialAsync(userId, serviceId, "Revoked", ct);

    public async Task<IReadOnlyList<UserServiceCredentialRecord>> GetServiceCredentialsAsync(Guid serviceId,
        bool eligibleOnly, CancellationToken ct)
    {
        var records = new List<UserServiceCredentialRecord>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          SELECT k.user_id,k.service_id,k.backend_type,k.nonce,k.ciphertext,k.tag,k.status,k.created_at_utc,k.updated_at_utc,
                                 COALESCE(g.direct_only,0)
                          FROM user_service_credentials k
                          JOIN users u ON u.id=k.user_id
                          LEFT JOIN user_groups g ON g.id=u.group_id
                          WHERE k.service_id=@service
                            AND (@eligible=0 OR (k.status='Active' AND u.enabled=1
                              AND (u.expires_at_utc IS NULL OR u.expires_at_utc>@now)
                              AND (u.traffic_limit_bytes IS NULL OR COALESCE((SELECT SUM(t.upload_bytes+t.download_bytes) FROM usage_totals t WHERE t.user_id=u.id),0)<u.traffic_limit_bytes)))
                          ORDER BY k.user_id;
                          """;
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        cmd.Parameters.AddWithValue("@eligible", eligibleOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var userId = SqliteValue.ToGuid(r.GetString(0));
            var id = SqliteValue.ToGuid(r.GetString(1));
            var backend = r.GetString(2);
            var credential = credentialProtector.Unprotect(userId, id, backend,
                new ProtectedCredential((byte[])r[3], (byte[])r[4], (byte[])r[5]));
            records.Add(new UserServiceCredentialRecord(userId, id, backend, credential, r.GetString(6),
                SqliteValue.ToDateTimeOffset(r.GetString(7)), SqliteValue.ToDateTimeOffset(r.GetString(8)),
                r.GetInt64(9) == 1));
        }
        return records;
    }

    public async Task<IReadOnlyList<Guid>> GetBoundServicesAsync(Guid userId, CancellationToken ct)
    {
        var ids = new List<Guid>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT service_id FROM user_service_bindings WHERE user_id=@user ORDER BY service_id;";
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(SqliteValue.ToGuid(r.GetString(0)));
        return ids;
    }

    /// <summary>A user's usage in the current period per service, with the names of the service and its node.</summary>
    public async Task<IReadOnlyList<PreviousUsageEntryRecord>> GetNamedUsageAsync(Guid userId, CancellationToken ct)
    {
        var rows = new List<PreviousUsageEntryRecord>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          SELECT t.service_id,COALESCE(n.display_name,''),COALESCE(s.name,''),a.country_code,t.upload_bytes,t.download_bytes
                          FROM usage_totals t
                          LEFT JOIN service_instances s ON s.id=t.service_id
                          LEFT JOIN nodes n ON n.id=s.node_id
                          LEFT JOIN agents a ON a.node_id=s.node_id
                          WHERE t.user_id=@user AND t.upload_bytes+t.download_bytes>0
                          ORDER BY t.upload_bytes+t.download_bytes DESC;
                          """;
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new PreviousUsageEntryRecord(SqliteValue.ToGuid(r.GetString(0)), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.GetInt64(4), r.GetInt64(5)));
        return rows;
    }

    /// <summary>Every user's last closed period (the usage their latest reset cleared), per service.</summary>
    public async Task<IReadOnlyList<PreviousUsagePeriodRecord>> GetPreviousUsageAsync(Guid? userId, CancellationToken ct)
    {
        var periods = new List<PreviousUsagePeriodRecord>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          SELECT u.id,u.previous_period_start_utc,u.previous_period_end_utc,
                                 p.service_id,p.node_name,p.service_name,p.country_code,p.upload_bytes,p.download_bytes
                          FROM users u LEFT JOIN usage_previous_period p ON p.user_id=u.id
                          WHERE u.previous_period_end_utc IS NOT NULL AND (@user IS NULL OR u.id=@user)
                          ORDER BY u.id,p.upload_bytes+p.download_bytes DESC;
                          """;
        cmd.Parameters.AddWithValue("@user", userId is null ? DBNull.Value : userId.Value.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = SqliteValue.ToGuid(r.GetString(0));
            if (periods.Count == 0 || periods[^1].UserId != id)
                periods.Add(new PreviousUsagePeriodRecord(id, SqliteValue.ToDateTimeOffset(r.GetString(1)),
                    SqliteValue.ToDateTimeOffset(r.GetString(2)), new List<PreviousUsageEntryRecord>()));
            if (r.IsDBNull(3)) continue;
            ((List<PreviousUsageEntryRecord>)periods[^1].Entries).Add(new PreviousUsageEntryRecord(
                SqliteValue.ToGuid(r.GetString(3)), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.GetInt64(7), r.GetInt64(8)));
        }
        return periods;
    }

    public async Task<IReadOnlyList<UsageTotalRecord>> GetUsageTotalsAsync(Guid? userId, Guid? serviceId,
        CancellationToken ct)
    {
        var rows = new List<UsageTotalRecord>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT user_id,service_id,upload_bytes,download_bytes,updated_at_utc FROM usage_totals WHERE (@user IS NULL OR user_id=@user) AND (@service IS NULL OR service_id=@service) ORDER BY user_id,service_id;";
        cmd.Parameters.AddWithValue("@user", userId is null ? DBNull.Value : userId.Value.ToString("D"));
        cmd.Parameters.AddWithValue("@service", serviceId is null ? DBNull.Value : serviceId.Value.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            rows.Add(new UsageTotalRecord(SqliteValue.ToGuid(r.GetString(0)), SqliteValue.ToGuid(r.GetString(1)),
                r.GetInt64(2), r.GetInt64(3), SqliteValue.ToDateTimeOffset(r.GetString(4))));
        return rows;
    }

    public async Task ClearServicePublicEndpointAsync(Guid nodeId, Guid serviceId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          DELETE FROM service_public_endpoints
                          WHERE service_id=@service AND EXISTS (SELECT 1 FROM service_instances WHERE id=@service AND node_id=@node);
                          """;
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        cmd.Parameters.AddWithValue("@node", nodeId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> SetServicePublicEndpointAsync(Guid nodeId, ServicePublicEndpointRecord endpoint,
        CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          INSERT INTO service_public_endpoints (service_id,host,port,tls_server_name,updated_at_utc)
                          SELECT @service,@host,@port,@tls,@updated
                          WHERE EXISTS (SELECT 1 FROM service_instances WHERE id=@service AND node_id=@node)
                          ON CONFLICT(service_id) DO UPDATE SET host=excluded.host,port=excluded.port,
                              tls_server_name=excluded.tls_server_name,updated_at_utc=excluded.updated_at_utc;
                          """;
        cmd.Parameters.AddWithValue("@service", endpoint.ServiceId.ToString("D"));
        cmd.Parameters.AddWithValue("@node", nodeId.ToString("D"));
        cmd.Parameters.AddWithValue("@host", endpoint.Host);
        cmd.Parameters.AddWithValue("@port", endpoint.Port);
        cmd.Parameters.AddWithValue("@tls", endpoint.TlsServerName is null ? DBNull.Value : endpoint.TlsServerName);
        cmd.Parameters.AddWithValue("@updated", SqliteValue.ToUtcText(endpoint.UpdatedAtUtc));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<ServicePublicEndpointRecord?> GetServicePublicEndpointAsync(Guid nodeId, Guid serviceId,
        CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          SELECT p.service_id,p.host,p.port,p.tls_server_name,p.updated_at_utc
                          FROM service_public_endpoints p JOIN service_instances s ON s.id=p.service_id
                          WHERE p.service_id=@service AND s.node_id=@node;
                          """;
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        cmd.Parameters.AddWithValue("@node", nodeId.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadPublicEndpoint(r) : null;
    }

    public async Task<IReadOnlyList<SubscriptionServiceRecord>?> GetSubscriptionServicesAsync(string token,
        CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var user = c.CreateCommand();
        user.CommandText = """
                           SELECT u.id,COALESCE(g.direct_only,0) FROM users u
                           LEFT JOIN user_groups g ON g.id=u.group_id
                           WHERE u.subscription_token_hash=@hash AND u.enabled=1
                             AND (u.expires_at_utc IS NULL OR u.expires_at_utc>@now)
                             AND (u.traffic_limit_bytes IS NULL OR
                                  COALESCE((SELECT SUM(t.upload_bytes + t.download_bytes) FROM usage_totals t WHERE t.user_id=u.id), 0) < u.traffic_limit_bytes);
                           """;
        user.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        user.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        string userId;
        bool directOnly;
        await using (var found = await user.ExecuteReaderAsync(ct))
        {
            if (!await found.ReadAsync(ct)) return null;
            userId = found.GetString(0);
            directOnly = found.GetInt64(1) == 1;
        }

        var services = new List<SubscriptionServiceRecord>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                           SELECT s.id,s.name,s.backend_type,s.config_json,
                                  COALESCE(p.host,CASE WHEN rn.id IS NULL THEN a.public_ipv4 ELSE ra.public_ipv4 END),
                                  COALESCE(p.port,CASE WHEN rn.id IS NULL THEN NULL ELSE CAST(json_extract(s.config_json,'$.relayPort') AS INTEGER) END,
                                           CAST(json_extract(s.config_json,'$.listenPort') AS INTEGER)),
                                  p.tls_server_name,COALESCE(p.updated_at_utc,a.last_seen_at_utc),
                                   k.nonce,k.ciphertext,k.tag,c.san,c.certificate_pem,n.display_name,a.city,
                                   CASE WHEN p.host IS NOT NULL OR s.ipv6_entry=0 THEN NULL WHEN rn.id IS NULL THEN a.public_ipv6 ELSE ra.public_ipv6 END,
                                   a.country_code,rs.exit_country_code,rs.route_exit_countries_json,a.region,n.city_override
                           FROM user_service_bindings b
                           JOIN service_instances s ON s.id=b.service_id
                           JOIN nodes n ON n.id=s.node_id
                           LEFT JOIN agents a ON a.node_id=s.node_id
                           LEFT JOIN service_public_endpoints p ON p.service_id=s.id
                           LEFT JOIN service_runtime_states rs ON rs.service_id=s.id
                           LEFT JOIN nodes rn ON lower(rn.id)=lower(json_extract(s.config_json,'$.relayNodeId'))
                           LEFT JOIN agents ra ON ra.node_id=rn.id
                           LEFT JOIN certificates c ON c.id=json_extract(s.config_json,'$.certificateId')
                           JOIN user_service_credentials k ON k.user_id=b.user_id AND k.service_id=b.service_id
                           WHERE b.user_id=@user AND s.enabled=1 AND k.status='Active'
                             AND (p.host IS NOT NULL OR CASE WHEN rn.id IS NULL
                                                                 THEN COALESCE(a.public_ipv4,CASE WHEN s.ipv6_entry=1 THEN a.public_ipv6 END)
                                                             ELSE COALESCE(ra.public_ipv4,CASE WHEN s.ipv6_entry=1 THEN ra.public_ipv6 END) END IS NOT NULL)
                             AND COALESCE(p.port,CASE WHEN rn.id IS NULL THEN NULL ELSE CAST(json_extract(s.config_json,'$.relayPort') AS INTEGER) END,
                                          CAST(json_extract(s.config_json,'$.listenPort') AS INTEGER)) BETWEEN 1 AND 65535
                           -- Grouped by node, then service, so a node's services sit together in the client.
                           ORDER BY n.display_name COLLATE NOCASE,s.name COLLATE NOCASE,s.id;
                          """;
        cmd.Parameters.AddWithValue("@user", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var serviceId = SqliteValue.ToGuid(r.GetString(0));
            var backend = r.GetString(2);
            var credential = credentialProtector.Unprotect(SqliteValue.ToGuid(userId), serviceId, backend,
                new ProtectedCredential((byte[])r[8], (byte[])r[9], (byte[])r[10]));
            // Without an explicit endpoint the node's public IPv4 (and IPv6, if any) is dialled and the certificate's first DNS name
            // is sent as SNI; a self-signed certificate is pinned by fingerprint instead of verified by a CA.
            var san = r.IsDBNull(11) ? null : r.GetString(11);
            var pem = r.IsDBNull(12) ? null : r.GetString(12);
            var serverName = r.IsDBNull(6) ? ServerNameFor(san, r.GetString(13), serviceId) : r.GetString(6);
            var nodeName = r.GetString(13);
            var city = SubscriptionNames.DisplayCity(r.IsDBNull(20) ? null : r.GetString(20),
                r.IsDBNull(14) ? null : r.GetString(14), r.IsDBNull(19) ? null : r.GetString(19));
            var country = r.IsDBNull(16) ? null : r.GetString(16);
            var protocol = BackendDefinitionCatalog.TryGet(backend, out var definition)
                ? definition.ServiceProtocol.ShortName : backend;
            var updated = SqliteValue.ToDateTimeOffset(r.GetString(7));
            // A node with a public IPv6 gets a second entry dialling it unless the service turned that off; an explicit
            // endpoint replaces both.
            var hosts = new List<(string Host, bool Ipv6)>(2);
            if (!r.IsDBNull(4)) hosts.Add((r.GetString(4), false));
            if (!r.IsDBNull(15)) hosts.Add((r.GetString(15), true));
            // A direct-only group sees only the lines that go out directly; the Agent admits no others for them.
            var lines = ServiceLines.Of(backend, r.GetString(3));
            if (directOnly && !lines.AnyDirect) continue;
            var routes = directOnly ? [.. lines.Routes.Where(static route => route.Proxy is null)] : lines.Routes;
            // Where each proxied line leaves, as the Agent found by looking up its address through the proxy.
            var exitCountry = r.IsDBNull(17) ? null : r.GetString(17);
            var routeCountries = routes.Count == 0 || r.IsDBNull(18) ? null : RouteCountries(r.GetString(18));
            foreach (var (host, ipv6) in hosts)
            {
                var direct = ipv6 ? SubscriptionGroup.DirectIpv6 : SubscriptionGroup.Direct;
                services.Add(new SubscriptionServiceRecord(SqliteValue.ToGuid(userId), serviceId,
                    lines.DefaultProxied
                        ? SubscriptionNames.Relay(country, city, nodeName, ipv6, r.GetString(1), exitCountry)
                        : SubscriptionNames.Direct(country, city, nodeName, protocol, ipv6),
                    backend, r.GetString(3), credential,
                    new ServicePublicEndpointRecord(serviceId, host, r.GetInt32(5), serverName, updated),
                    SelfSignedFingerprint(pem),
                    routes.Count == 0 ? null : routes.Select(route => route.Proxy is null
                        ? new SubscriptionRouteEntry(route.Id,
                            SubscriptionNames.Direct(country, city, nodeName, protocol, ipv6, route.Name), direct)
                        : new SubscriptionRouteEntry(route.Id,
                            SubscriptionNames.Relay(country, city, nodeName, ipv6, route.Name,
                                routeCountries?.GetValueOrDefault(route.Id)), SubscriptionGroup.Relay))
                        .ToArray(),
                    IncludeDefault: !(directOnly && lines.DefaultProxied),
                    DefaultGroup: lines.DefaultProxied ? SubscriptionGroup.Relay : direct));
            }
        }

        return services;
    }

    private static Dictionary<Guid, string>? RouteCountries(string json)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize(json,
                    HyPanelJsonSerializerContext.Default.RouteExitCountryArray)?
                .ToDictionary(item => item.RouteId, item => item.CountryCode);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Usage, limit and expiry for the Clash <c>subscription-userinfo</c> header.</summary>
    public async Task<SubscriptionUserInfo?> GetSubscriptionUserInfoAsync(string token, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(t.upload_bytes),0),COALESCE(SUM(t.download_bytes),0),u.traffic_limit_bytes,u.expires_at_utc,u.username
            FROM users u LEFT JOIN usage_totals t ON t.user_id=u.id
            WHERE u.subscription_token_hash=@hash GROUP BY u.id;
            """;
        cmd.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SubscriptionUserInfo(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2),
            r.IsDBNull(3) ? null : SqliteValue.ToDateTimeOffset(r.GetString(3)), r.GetString(4));
    }

    /// <summary>
    /// SNI for a subscription entry: the certificate's first concrete DNS name, or, when it only covers a wildcard
    /// (<c>*.example.com</c>), a name under that wildcard derived from the node. Hysteria rejects handshakes whose
    /// SNI is not covered by the certificate, so an empty SNI would never connect. The SNI travels in clear text, so
    /// the node name goes in as lower-case hex (<c>jp-nb</c> → <c>6a702d6e62</c>) instead of spelling it out.
    /// </summary>
    /// <remarks>
    /// A node without a name gets a random-looking label derived from <paramref name="seed"/> (the service), so it is
    /// the same on every subscription fetch; without a seed it is freshly random.
    /// </remarks>
    internal static string? ServerNameFor(string? san, string nodeName, Guid? seed = null)
    {
        if (FirstDnsName(san) is { } concrete) return concrete;
        var wildcard = DnsNames(san).FirstOrDefault(name => name.StartsWith("*.", StringComparison.Ordinal));
        if (wildcard is null) return null;
        var suffix = wildcard[1..];
        // A DNS label holds at most 63 characters and a whole name 253; whole bytes only, so cut at an even length.
        var room = Math.Min(62, (253 - suffix.Length) & ~1);
        var label = Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(nodeName.Trim()));
        if (label.Length > room) label = label[..room];
        if (label.Length == 0)
            label = Convert.ToHexStringLower(seed is { } value
                ? System.Security.Cryptography.SHA256.HashData(value.ToByteArray())[..5]
                : System.Security.Cryptography.RandomNumberGenerator.GetBytes(5));
        return label + suffix;
    }

    private static IEnumerable<string> DnsNames(string? san)
    {
        if (string.IsNullOrWhiteSpace(san)) yield break;
        foreach (var part in san.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOfAny([':', '=']);
            if (separator > 0 && part[..separator].Trim().Equals("DNS", StringComparison.OrdinalIgnoreCase) &&
                part[(separator + 1)..].Trim() is { Length: > 0 } name)
                yield return name;
        }
    }

    /// <summary>The name an ACME certificate is requested for, wildcard included (<c>*.example.com</c>).</summary>
    internal static string? AcmeDomain(string? san) => DnsNames(san).FirstOrDefault();

    internal static string? FirstDnsName(string? san)
    {
        if (string.IsNullOrWhiteSpace(san)) return null;
        foreach (var part in san.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOfAny([':', '=']);
            if (separator > 0 && part[..separator].Trim().Equals("DNS", StringComparison.OrdinalIgnoreCase))
            {
                var name = part[(separator + 1)..].Trim();
                if (name.Length > 0 && !name.StartsWith('*')) return name;
            }
        }
        return null;
    }

    private static string? SelfSignedFingerprint(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return null;
        try
        {
            using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
            return certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData)
                ? Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant()
                : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private async Task<UserServiceCredentialRecord?> ReplaceServiceCredentialAsync(Guid userId, Guid serviceId,
        string status, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        var backend = await GetBindingBackendAsync(c, tx, userId, serviceId, requireBinding: true, ct);
        if (!BackendCapabilities.IsMultiUser(backend))
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        var now = timeProvider.GetUtcNow();
        var plaintext = Guid.NewGuid().ToString("D");
        var protectedCredential = credentialProtector.Protect(userId, serviceId, backend, plaintext);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
                          UPDATE user_service_credentials SET nonce=@nonce,ciphertext=@ciphertext,tag=@tag,status=@status,updated_at_utc=@now
                          WHERE user_id=@user AND service_id=@service;
                          """;
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        cmd.Parameters.Add("@nonce", SqliteType.Blob).Value = protectedCredential.Nonce;
        cmd.Parameters.Add("@ciphertext", SqliteType.Blob).Value = protectedCredential.Ciphertext;
        cmd.Parameters.Add("@tag", SqliteType.Blob).Value = protectedCredential.Tag;
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        if (await cmd.ExecuteNonQueryAsync(ct) != 1)
        {
            await tx.RollbackAsync(ct);
            return null;
        }
        await IncrementServiceNodeRevisionAsync(c, tx, serviceId, ct);
        await tx.CommitAsync(ct);
        return new UserServiceCredentialRecord(userId, serviceId, backend, plaintext, status, now, now);
    }

    private async Task UpsertCredentialAsync(SqliteConnection c, SqliteTransaction tx, Guid userId, Guid serviceId,
        string backend, string plaintext, DateTimeOffset now, CancellationToken ct)
    {
        var protectedCredential = credentialProtector.Protect(userId, serviceId, backend, plaintext);
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO user_service_credentials (user_id,service_id,backend_type,nonce,ciphertext,tag,status,desired_eligible,created_at_utc,updated_at_utc)
            VALUES (@user,@service,@backend,@nonce,@ciphertext,@tag,'Active',1,@now,@now)
            ON CONFLICT(user_id,service_id) DO UPDATE SET backend_type=excluded.backend_type,nonce=excluded.nonce,
              ciphertext=excluded.ciphertext,tag=excluded.tag,status='Active',desired_eligible=1,updated_at_utc=excluded.updated_at_utc;
            """;
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        cmd.Parameters.AddWithValue("@backend", backend);
        cmd.Parameters.Add("@nonce", SqliteType.Blob).Value = protectedCredential.Nonce;
        cmd.Parameters.Add("@ciphertext", SqliteType.Blob).Value = protectedCredential.Ciphertext;
        cmd.Parameters.Add("@tag", SqliteType.Blob).Value = protectedCredential.Tag;
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string?> GetBindingBackendAsync(SqliteConnection c, SqliteTransaction tx, Guid userId,
        Guid serviceId, bool requireBinding, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT s.backend_type FROM users u CROSS JOIN service_instances s WHERE u.id=@user AND s.id=@service AND (@requireBinding=0 OR EXISTS (SELECT 1 FROM user_service_bindings b WHERE b.user_id=u.id AND b.service_id=s.id));";
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        cmd.Parameters.AddWithValue("@requireBinding", requireBinding ? 1 : 0);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task IncrementServiceNodeRevisionAsync(SqliteConnection c, SqliteTransaction tx,
        Guid serviceId, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE nodes SET desired_revision=desired_revision+1 WHERE id=(SELECT node_id FROM service_instances WHERE id=@service);";
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task IncrementBoundNodeRevisionsAsync(SqliteConnection c, SqliteTransaction tx, Guid userId,
        CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE nodes SET desired_revision=desired_revision+1 WHERE id IN (SELECT DISTINCT s.node_id FROM user_service_bindings b JOIN service_instances s ON s.id=b.service_id WHERE b.user_id=@user);";
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static byte[] TokenHash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    private static ServicePublicEndpointRecord ReadPublicEndpoint(SqliteDataReader r) =>
        new(SqliteValue.ToGuid(r.GetString(0)), r.GetString(1), r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetString(3), SqliteValue.ToDateTimeOffset(r.GetString(4)));

    /// <summary>Regional-indicator flag emoji for an ISO 3166-1 alpha-2 code ("GB" → 🇬🇧).</summary>
    internal static string CountryFlag(string code) => code.Length == 2 && char.IsAsciiLetter(code[0]) &&
                                                       char.IsAsciiLetter(code[1])
        ? char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(code[0]) - 'A') +
          char.ConvertFromUtf32(0x1F1E6 + char.ToUpperInvariant(code[1]) - 'A')
        : string.Empty;

    private static UserRecord ReadUser(SqliteDataReader r) => new(SqliteValue.ToGuid(r.GetString(0)), r.GetString(1),
        r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetInt64(5),
        r.IsDBNull(6) ? null : SqliteValue.ToDateTimeOffset(r.GetString(6)),
        SqliteValue.ToDateTimeOffset(r.GetString(7)), SqliteValue.ToDateTimeOffset(r.GetString(8)),
        r.FieldCount > 9 && !r.IsDBNull(9) ? r.GetInt32(9) : null);

    /// <summary>
    /// Sets the day of month (1-28) the user's usage restarts, or null to never reset. The period starts now, so
    /// enabling a reset day never wipes the current month's usage straight away.
    /// </summary>
    public async Task SetTrafficResetDayAsync(Guid userId, int? day, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET traffic_reset_day=@day, traffic_reset_at_utc=CASE WHEN @day IS NULL THEN NULL ELSE COALESCE(traffic_reset_at_utc,@now) END WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@day", day is null ? DBNull.Value : day.Value);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Clears the usage of every user whose monthly reset boundary (00:00 UTC on the reset day) has passed since
    /// their last reset, and re-syncs their nodes so credentials cut off by the traffic limit come back.
    /// </summary>
    public async Task<int> ResetDueTrafficAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var due = new List<Guid>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using (var select = c.CreateCommand())
        {
            select.CommandText = "SELECT id,traffic_reset_day,traffic_reset_at_utc FROM users WHERE traffic_reset_day IS NOT NULL;";
            await using var r = await select.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var last = r.IsDBNull(2) ? (DateTimeOffset?)null : SqliteValue.ToDateTimeOffset(r.GetString(2));
                if (last is null || last < LastTrafficResetBoundary(now, r.GetInt32(1)))
                    due.Add(SqliteValue.ToGuid(r.GetString(0)));
            }
        }

        foreach (var userId in due) await ResetTrafficAsync(c, userId, now, ct);
        return due.Count;
    }

    /// <summary>Clears the user's usage now; with a monthly reset day the current period restarts too.</summary>
    public async Task<bool> ResetTrafficAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        return await ResetTrafficAsync(c, userId, timeProvider.GetUtcNow(), ct);
    }

    private static async Task<bool> ResetTrafficAsync(SqliteConnection c, Guid userId, DateTimeOffset now,
        CancellationToken ct)
    {
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using (var keep = c.CreateCommand())
        {
            // The period being closed, per service, replaces the one kept from the reset before.
            keep.Transaction = tx;
            keep.CommandText = """
                               UPDATE users SET previous_period_start_utc=COALESCE(traffic_reset_at_utc,previous_period_end_utc,created_at_utc),
                                                previous_period_end_utc=@now
                               WHERE id=@id;
                               DELETE FROM usage_previous_period WHERE user_id=@id;
                               INSERT INTO usage_previous_period (user_id,service_id,node_name,service_name,country_code,upload_bytes,download_bytes)
                               SELECT t.user_id,t.service_id,COALESCE(n.display_name,''),COALESCE(s.name,''),a.country_code,t.upload_bytes,t.download_bytes
                               FROM usage_totals t
                               LEFT JOIN service_instances s ON s.id=t.service_id
                               LEFT JOIN nodes n ON n.id=s.node_id
                               LEFT JOIN agents a ON a.node_id=s.node_id
                               WHERE t.user_id=@id AND t.upload_bytes+t.download_bytes>0;
                               """;
            keep.Parameters.AddWithValue("@id", userId.ToString("D"));
            keep.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
            await keep.ExecuteNonQueryAsync(ct);
        }
        await using (var mark = c.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = "UPDATE users SET traffic_reset_at_utc=CASE WHEN traffic_reset_day IS NULL THEN NULL ELSE @now END WHERE id=@id;";
            mark.Parameters.AddWithValue("@id", userId.ToString("D"));
            mark.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
            if (await mark.ExecuteNonQueryAsync(ct) != 1)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
        }
        await using (var clear = c.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM usage_totals WHERE user_id=@id;";
            clear.Parameters.AddWithValue("@id", userId.ToString("D"));
            await clear.ExecuteNonQueryAsync(ct);
        }
        // Credentials cut off by the traffic limit come back on the next sync.
        await IncrementBoundNodeRevisionsAsync(c, tx, userId, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    internal static DateTimeOffset LastTrafficResetBoundary(DateTimeOffset now, int day)
    {
        var boundary = new DateTimeOffset(now.UtcDateTime.Year, now.UtcDateTime.Month, day, 0, 0, 0, TimeSpan.Zero);
        return boundary <= now ? boundary : boundary.AddMonths(-1);
    }
}
