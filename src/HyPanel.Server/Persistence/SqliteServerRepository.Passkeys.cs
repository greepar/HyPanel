namespace HyPanel.Server.Persistence;

using Microsoft.Data.Sqlite;

internal sealed record PasskeyRecord(string Id, Guid UserId, string Name, byte[] PublicKey, int Algorithm,
    long SignCount, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastUsedAtUtc);

internal sealed partial class SqliteServerRepository
{
    private const string PasskeyColumns =
        "id,user_id,name,public_key,algorithm,sign_count,created_at_utc,last_used_at_utc";

    public async Task<PasskeyRecord> AddPasskeyAsync(Guid userId, string credentialId, string name, byte[] publicKey,
        int algorithm, long signCount, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            $"INSERT INTO user_passkeys ({PasskeyColumns}) VALUES (@id,@user,@name,@key,@alg,@count,@now,NULL);";
        cmd.Parameters.AddWithValue("@id", credentialId);
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.Add("@key", SqliteType.Blob).Value = publicKey;
        cmd.Parameters.AddWithValue("@alg", algorithm);
        cmd.Parameters.AddWithValue("@count", signCount);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        await cmd.ExecuteNonQueryAsync(ct);
        return new PasskeyRecord(credentialId, userId, name, publicKey, algorithm, signCount, now, null);
    }

    public async Task<IReadOnlyList<PasskeyRecord>> GetPasskeysAsync(Guid userId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {PasskeyColumns} FROM user_passkeys WHERE user_id=@user ORDER BY created_at_utc;";
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var result = new List<PasskeyRecord>();
        while (await r.ReadAsync(ct)) result.Add(ReadPasskey(r));
        return result;
    }

    public async Task<PasskeyRecord?> GetPasskeyAsync(string credentialId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {PasskeyColumns} FROM user_passkeys WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", credentialId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadPasskey(r) : null;
    }

    public async Task MarkPasskeyUsedAsync(string credentialId, long signCount, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE user_passkeys SET sign_count=@count, last_used_at_utc=@now WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", credentialId);
        cmd.Parameters.AddWithValue("@count", signCount);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeletePasskeyAsync(Guid userId, string credentialId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM user_passkeys WHERE id=@id AND user_id=@user;";
        cmd.Parameters.AddWithValue("@id", credentialId);
        cmd.Parameters.AddWithValue("@user", userId.ToString("D"));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static PasskeyRecord ReadPasskey(SqliteDataReader r) => new(r.GetString(0), SqliteValue.ToGuid(r.GetString(1)),
        r.GetString(2), (byte[])r.GetValue(3), r.GetInt32(4), r.GetInt64(5), SqliteValue.ToDateTimeOffset(r.GetString(6)),
        r.IsDBNull(7) ? null : SqliteValue.ToDateTimeOffset(r.GetString(7)));
}
