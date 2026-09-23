namespace HyPanel.Server.Persistence;

using System.Security.Cryptography;
using System.Text;
using HyPanel.Server.Security;
using Microsoft.Data.Sqlite;

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
            "SELECT id,username,normalized_username,role,enabled,traffic_limit_bytes,expires_at_utc,created_at_utc,updated_at_utc FROM users WHERE normalized_username=@name;";
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
            "SELECT id,username,normalized_username,password_hash,role,enabled,traffic_limit_bytes,expires_at_utc,created_at_utc,updated_at_utc FROM users WHERE normalized_username=@name;";
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
            "SELECT u.id,u.username,u.normalized_username,u.role,u.enabled,u.traffic_limit_bytes,u.expires_at_utc,u.created_at_utc,u.updated_at_utc FROM user_sessions s JOIN users u ON u.id=s.user_id WHERE s.token_hash=@hash AND s.revoked_at_utc IS NULL AND s.expires_at_utc>@now AND u.enabled=1 AND (u.expires_at_utc IS NULL OR u.expires_at_utc>@now);";
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
            "SELECT id,username,normalized_username,role,enabled,traffic_limit_bytes,expires_at_utc,created_at_utc,updated_at_utc FROM users ORDER BY created_at_utc,id;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) users.Add(ReadUser(r));
        return users;
    }

    public async Task<UserRecord?> GetUserAsync(Guid id, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "SELECT id,username,normalized_username,role,enabled,traffic_limit_bytes,expires_at_utc,created_at_utc,updated_at_utc FROM users WHERE id=@id;";
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
            foreach (var table in new[] { "usage_totals", "user_service_credentials", "user_service_bindings", "user_sessions" })
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
        if (!HyPanel.Server.Backends.BackendCapabilities.IsMultiUser(backend))
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
            select.CommandText = """
                SELECT b.user_id,b.service_id,s.node_id,s.backend_type
                FROM user_service_bindings b
                JOIN service_instances s ON s.id=b.service_id
                LEFT JOIN user_service_credentials k ON k.user_id=b.user_id AND k.service_id=b.service_id
                WHERE k.user_id IS NULL AND s.backend_type IN ('xray','xray-ss','hysteria2')
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
                          SELECT k.user_id,k.service_id,k.backend_type,k.nonce,k.ciphertext,k.tag,k.status,k.created_at_utc,k.updated_at_utc
                          FROM user_service_credentials k
                          JOIN users u ON u.id=k.user_id
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
                SqliteValue.ToDateTimeOffset(r.GetString(7)), SqliteValue.ToDateTimeOffset(r.GetString(8))));
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
                           SELECT u.id FROM users u
                           WHERE u.subscription_token_hash=@hash AND u.enabled=1
                             AND (u.expires_at_utc IS NULL OR u.expires_at_utc>@now)
                             AND (u.traffic_limit_bytes IS NULL OR
                                  COALESCE((SELECT SUM(t.upload_bytes + t.download_bytes) FROM usage_totals t WHERE t.user_id=u.id), 0) < u.traffic_limit_bytes);
                           """;
        user.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        user.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        var userId = await user.ExecuteScalarAsync(ct) as string;
        if (userId is null) return null;

        var services = new List<SubscriptionServiceRecord>();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                           SELECT s.id,n.display_name || ' · ' || s.name,s.backend_type,s.config_json,
                                  COALESCE(p.host,a.public_ipv4),
                                  COALESCE(p.port,CAST(json_extract(s.config_json,'$.listenPort') AS INTEGER)),
                                  p.tls_server_name,COALESCE(p.updated_at_utc,a.last_seen_at_utc),
                                   k.nonce,k.ciphertext,k.tag,c.san,c.certificate_pem,n.display_name
                           FROM user_service_bindings b
                           JOIN service_instances s ON s.id=b.service_id
                           JOIN nodes n ON n.id=s.node_id
                           LEFT JOIN agents a ON a.node_id=s.node_id
                           LEFT JOIN service_public_endpoints p ON p.service_id=s.id
                           LEFT JOIN certificates c ON c.id=json_extract(s.config_json,'$.certificateId')
                           JOIN user_service_credentials k ON k.user_id=b.user_id AND k.service_id=b.service_id
                           WHERE b.user_id=@user AND s.enabled=1 AND k.status='Active'
                             AND (p.host IS NOT NULL OR a.public_ipv4 IS NOT NULL)
                             AND COALESCE(p.port,CAST(json_extract(s.config_json,'$.listenPort') AS INTEGER)) BETWEEN 1 AND 65535
                           ORDER BY s.name,s.id;
                          """;
        cmd.Parameters.AddWithValue("@user", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var serviceId = SqliteValue.ToGuid(r.GetString(0));
            var backend = r.GetString(2);
            var credential = credentialProtector.Unprotect(SqliteValue.ToGuid(userId), serviceId, backend,
                new ProtectedCredential((byte[])r[8], (byte[])r[9], (byte[])r[10]));
            // Without an explicit endpoint the node's public IPv4 is dialled and the certificate's first DNS name
            // is sent as SNI; a self-signed certificate is pinned by fingerprint instead of verified by a CA.
            var san = r.IsDBNull(11) ? null : r.GetString(11);
            var pem = r.IsDBNull(12) ? null : r.GetString(12);
            var serverName = r.IsDBNull(6) ? ServerNameFor(san, r.GetString(13)) : r.GetString(6);
            services.Add(new SubscriptionServiceRecord(SqliteValue.ToGuid(userId), serviceId, r.GetString(1), backend, r.GetString(3), credential,
                new ServicePublicEndpointRecord(serviceId, r.GetString(4), r.GetInt32(5),
                    serverName, SqliteValue.ToDateTimeOffset(r.GetString(7))), SelfSignedFingerprint(pem)));
        }

        return services;
    }

    /// <summary>Usage, limit and expiry for the Clash <c>subscription-userinfo</c> header.</summary>
    public async Task<SubscriptionUserInfo?> GetSubscriptionUserInfoAsync(string token, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(t.upload_bytes),0),COALESCE(SUM(t.download_bytes),0),u.traffic_limit_bytes,u.expires_at_utc
            FROM users u LEFT JOIN usage_totals t ON t.user_id=u.id
            WHERE u.subscription_token_hash=@hash GROUP BY u.id;
            """;
        cmd.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new SubscriptionUserInfo(r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt64(2),
            r.IsDBNull(3) ? null : SqliteValue.ToDateTimeOffset(r.GetString(3)));
    }

    /// <summary>
    /// SNI for a subscription entry: the certificate's first concrete DNS name, or, when it only covers a wildcard
    /// (<c>*.example.com</c>), a name under that wildcard derived from the node (<c>uk.example.com</c>). Hysteria
    /// rejects handshakes whose SNI is not covered by the certificate, so an empty SNI would never connect.
    /// </summary>
    internal static string? ServerNameFor(string? san, string nodeName)
    {
        if (FirstDnsName(san) is { } concrete) return concrete;
        var wildcard = DnsNames(san).FirstOrDefault(name => name.StartsWith("*.", StringComparison.Ordinal));
        if (wildcard is null) return null;
        var label = new string(nodeName.ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-').ToArray()).Trim('-');
        if (label.Length is 0 or > 63) label = "hy2";
        return label + wildcard[1..];
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
                ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.RawData)).ToLowerInvariant()
                : null;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static async Task<bool> IsBoundAsync(SqliteConnection c, Guid user, Guid service, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM user_service_bindings WHERE user_id=@user AND service_id=@service;";
        cmd.Parameters.AddWithValue("@user", user.ToString("D"));
        cmd.Parameters.AddWithValue("@service", service.ToString("D"));
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private async Task<UserServiceCredentialRecord?> ReplaceServiceCredentialAsync(Guid userId, Guid serviceId,
        string status, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        var backend = await GetBindingBackendAsync(c, tx, userId, serviceId, requireBinding: true, ct);
        if (!HyPanel.Server.Backends.BackendCapabilities.IsMultiUser(backend))
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

    private static UserRecord ReadUser(SqliteDataReader r) => new(SqliteValue.ToGuid(r.GetString(0)), r.GetString(1),
        r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.IsDBNull(5) ? null : r.GetInt64(5),
        r.IsDBNull(6) ? null : SqliteValue.ToDateTimeOffset(r.GetString(6)),
        SqliteValue.ToDateTimeOffset(r.GetString(7)), SqliteValue.ToDateTimeOffset(r.GetString(8)));
}
