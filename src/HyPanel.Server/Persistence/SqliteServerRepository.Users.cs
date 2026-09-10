namespace HyPanel.Server.Persistence;

using System.Security.Cryptography;
using System.Text;
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

    public async Task<string?> RotateSubscriptionTokenAsync(Guid id, string token, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE users SET subscription_token_hash=@hash,updated_at_utc=@now WHERE id=@id;";
        cmd.Parameters.Add("@hash", SqliteType.Blob).Value = TokenHash(token);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? token : null;
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
        await using var cmd = c.CreateCommand();
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
        return await cmd.ExecuteNonQueryAsync(ct) == 1 ? await GetUserAsync(id, ct) : null;
    }

    public async Task<bool> DeleteUserAsync(Guid id, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            foreach (var table in new[] { "usage_totals", "user_service_bindings", "user_sessions" })
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
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "INSERT INTO user_service_bindings (user_id,service_id,created_at_utc) SELECT @user,@service,@now WHERE EXISTS (SELECT 1 FROM users WHERE id=@user) AND EXISTS (SELECT 1 FROM service_instances WHERE id=@service) ON CONFLICT(user_id,service_id) DO NOTHING;";
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        await cmd.ExecuteNonQueryAsync(ct);
        return await IsBoundAsync(c, userId, serviceId, ct);
    }

    public async Task<bool> UnbindServiceAsync(Guid userId, Guid serviceId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM user_service_bindings WHERE user_id=@user AND service_id=@service;";
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
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
                          SELECT s.id,s.name,s.backend_type,s.config_json,p.host,p.port,p.tls_server_name,p.updated_at_utc
                          FROM user_service_bindings b
                          JOIN service_instances s ON s.id=b.service_id
                          JOIN service_public_endpoints p ON p.service_id=s.id
                          WHERE b.user_id=@user AND s.enabled=1
                          ORDER BY s.name,s.id;
                          """;
        cmd.Parameters.AddWithValue("@user", userId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var serviceId = SqliteValue.ToGuid(r.GetString(0));
            services.Add(new SubscriptionServiceRecord(serviceId, r.GetString(1), r.GetString(2), r.GetString(3),
                new ServicePublicEndpointRecord(serviceId, r.GetString(4), r.GetInt32(5),
                    r.IsDBNull(6) ? null : r.GetString(6), SqliteValue.ToDateTimeOffset(r.GetString(7)))));
        }

        return services;
    }

    private static async Task<bool> IsBoundAsync(SqliteConnection c, Guid user, Guid service, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM user_service_bindings WHERE user_id=@user AND service_id=@service;";
        cmd.Parameters.AddWithValue("@user", user.ToString("D"));
        cmd.Parameters.AddWithValue("@service", service.ToString("D"));
        return await cmd.ExecuteScalarAsync(ct) is not null;
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