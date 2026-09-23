namespace HyPanel.Server.Persistence;

using HyPanel.Server.Backends;
using Microsoft.Data.Sqlite;

internal sealed record UserGroupRecord(Guid Id, string Name, bool IsDefault, bool AutoIncludeNewServices,
    IReadOnlyList<Guid> ServiceIds, int MemberCount);

/// <summary>
/// Group-based access. A user's service bindings (and therefore their proxy credentials) always mirror their
/// group's service list; <see cref="SyncUserBindingsAsync"/> converges the two.
/// </summary>
internal sealed partial class SqliteServerRepository
{
    public static readonly Guid DefaultGroupId = Guid.Parse(SqliteMigrationRunner.DefaultGroupId);

    public async Task<IReadOnlyList<UserGroupRecord>> GetGroupsAsync(CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        var services = new Dictionary<Guid, List<Guid>>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT group_id,service_id FROM user_group_services ORDER BY service_id;";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var group = SqliteValue.ToGuid(r.GetString(0));
                if (!services.TryGetValue(group, out var list)) services[group] = list = [];
                list.Add(SqliteValue.ToGuid(r.GetString(1)));
            }
        }
        var groups = new List<UserGroupRecord>();
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT g.id,g.name,g.is_default,g.auto_include_new_services,
                  (SELECT COUNT(*) FROM users u WHERE u.group_id=g.id)
                FROM user_groups g ORDER BY g.is_default DESC,g.normalized_name;
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = SqliteValue.ToGuid(r.GetString(0));
                groups.Add(new UserGroupRecord(id, r.GetString(1), r.GetInt64(2) == 1, r.GetInt64(3) == 1,
                    services.TryGetValue(id, out var list) ? list : [], r.GetInt32(4)));
            }
        }
        return groups;
    }

    public async Task<Dictionary<Guid, Guid>> GetUserGroupMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<Guid, Guid>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id,group_id FROM users WHERE group_id IS NOT NULL;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) map[SqliteValue.ToGuid(r.GetString(0))] = SqliteValue.ToGuid(r.GetString(1));
        return map;
    }

    /// <summary>Creates or updates a group. Returns false on a duplicate name or unknown group.</summary>
    public async Task<bool> SaveGroupAsync(Guid id, string name, bool autoInclude, IReadOnlyList<Guid> serviceIds,
        bool create, CancellationToken ct)
    {
        await using (var c = await connectionFactory.OpenAsync(ct))
        await using (var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct))
        {
            try
            {
                await using (var cmd = c.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = create
                        ? "INSERT INTO user_groups (id,name,normalized_name,is_default,auto_include_new_services,created_at_utc) VALUES (@id,@name,@normalized,0,@auto,@now);"
                        : "UPDATE user_groups SET name=@name,normalized_name=@normalized,auto_include_new_services=@auto WHERE id=@id;";
                    cmd.Parameters.AddWithValue("@id", id.ToString("D"));
                    cmd.Parameters.AddWithValue("@name", name);
                    cmd.Parameters.AddWithValue("@normalized", name.ToUpperInvariant());
                    cmd.Parameters.AddWithValue("@auto", autoInclude ? 1 : 0);
                    cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
                    if (await cmd.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return false; }
                }
                await using (var clear = c.CreateCommand())
                {
                    clear.Transaction = tx;
                    clear.CommandText = "DELETE FROM user_group_services WHERE group_id=@id;";
                    clear.Parameters.AddWithValue("@id", id.ToString("D"));
                    await clear.ExecuteNonQueryAsync(ct);
                }
                foreach (var serviceId in serviceIds.Distinct())
                {
                    await using var insert = c.CreateCommand();
                    insert.Transaction = tx;
                    insert.CommandText = $"""
                        INSERT INTO user_group_services (group_id,service_id)
                        SELECT @group,id FROM service_instances WHERE id=@service AND backend_type IN {BackendCapabilities.MultiUserSqlList};
                        """;
                    insert.Parameters.AddWithValue("@group", id.ToString("D"));
                    insert.Parameters.AddWithValue("@service", serviceId.ToString("D"));
                    await insert.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            catch (SqliteException e) when (e.SqliteErrorCode == 19)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return false;
            }
        }
        await SyncGroupMembersAsync(id, ct);
        return true;
    }

    /// <summary>Deletes a non-default group; its members move to the default group. Null means "is default".</summary>
    public async Task<bool?> DeleteGroupAsync(Guid id, CancellationToken ct)
    {
        if (id == DefaultGroupId) return null;
        List<Guid> members;
        await using (var c = await connectionFactory.OpenAsync(ct))
        await using (var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct))
        {
            members = await ReadGuidsAsync(c, tx, "SELECT id FROM users WHERE group_id=@id;", id, ct);
            foreach (var sql in new[]
                     {
                         "UPDATE users SET group_id=@default WHERE group_id=@id;",
                         "DELETE FROM user_group_services WHERE group_id=@id;"
                     })
            {
                await using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = sql;
                cmd.Parameters.AddWithValue("@id", id.ToString("D"));
                cmd.Parameters.AddWithValue("@default", DefaultGroupId.ToString("D"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await using (var delete = c.CreateCommand())
            {
                delete.Transaction = tx;
                delete.CommandText = "DELETE FROM user_groups WHERE id=@id AND is_default=0;";
                delete.Parameters.AddWithValue("@id", id.ToString("D"));
                if (await delete.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return false; }
            }
            await tx.CommitAsync(ct);
        }
        foreach (var member in members) await SyncUserBindingsAsync(member, ct);
        return true;
    }

    public async Task<bool> SetUserGroupAsync(Guid userId, Guid groupId, CancellationToken ct)
    {
        await using (var c = await connectionFactory.OpenAsync(ct))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE users SET group_id=@group WHERE id=@user AND EXISTS (SELECT 1 FROM user_groups WHERE id=@group);";
            cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
            cmd.Parameters.AddWithValue("@group", groupId.ToString("D"));
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        await SyncUserBindingsAsync(userId, ct);
        return true;
    }

    /// <summary>Adds a new service to every auto-including group and grants it to their members.</summary>
    public async Task AddServiceToAutoGroupsAsync(Guid serviceId, CancellationToken ct)
    {
        var groups = await ReadGuidsAsync("SELECT id FROM user_groups WHERE auto_include_new_services=1;", ct);
        await using (var c = await connectionFactory.OpenAsync(ct))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"""
                INSERT OR IGNORE INTO user_group_services (group_id,service_id)
                SELECT g.id,s.id FROM user_groups g JOIN service_instances s ON s.id=@id
                WHERE g.auto_include_new_services=1 AND s.backend_type IN {BackendCapabilities.MultiUserSqlList};
                """;
            cmd.Parameters.AddWithValue("@id", serviceId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var group in groups) await SyncGroupMembersAsync(group, ct);
    }

    /// <summary>Re-applies every user's group at startup so bindings never drift from group definitions.</summary>
    public async Task SyncAllUserBindingsAsync(CancellationToken ct)
    {
        await using (var c = await connectionFactory.OpenAsync(ct))
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE users SET group_id=@default WHERE group_id IS NULL;";
            cmd.Parameters.AddWithValue("@default", DefaultGroupId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var userId in await ReadGuidsAsync("SELECT id FROM users ORDER BY id;", ct))
            await SyncUserBindingsAsync(userId, ct);
    }

    private async Task SyncGroupMembersAsync(Guid groupId, CancellationToken ct)
    {
        List<Guid> members;
        await using (var c = await connectionFactory.OpenAsync(ct))
            members = await ReadGuidsAsync(c, null, "SELECT id FROM users WHERE group_id=@id ORDER BY id;", groupId, ct);
        foreach (var member in members) await SyncUserBindingsAsync(member, ct);
    }

    /// <summary>Binds the user's group services they lack and revokes bindings their group no longer grants.</summary>
    public async Task SyncUserBindingsAsync(Guid userId, CancellationToken ct)
    {
        List<Guid> desired, current;
        await using (var c = await connectionFactory.OpenAsync(ct))
        {
            desired = await ReadGuidsAsync(c, null, $"""
                SELECT gs.service_id FROM users u JOIN user_group_services gs ON gs.group_id=u.group_id
                JOIN service_instances s ON s.id=gs.service_id
                WHERE u.id=@id AND s.backend_type IN {BackendCapabilities.MultiUserSqlList};
                """, userId, ct);
            current = await ReadGuidsAsync(c, null, "SELECT service_id FROM user_service_bindings WHERE user_id=@id;", userId, ct);
        }
        foreach (var serviceId in desired.Except(current)) await BindServiceAsync(userId, serviceId, ct);
        foreach (var serviceId in current.Except(desired)) await UnbindServiceAsync(userId, serviceId, ct);
    }

    private static async Task<List<Guid>> ReadGuidsAsync(SqliteConnection c, SqliteTransaction? tx, string sql, Guid id,
        CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        var ids = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) ids.Add(SqliteValue.ToGuid(r.GetString(0)));
        return ids;
    }
}
