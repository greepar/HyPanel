namespace HyPanel.Server.Persistence;

internal sealed partial class SqliteServerRepository
{
    /// <summary>
    /// Sets the day of month (1-28) the node's traffic counter restarts, or null to never reset. Like a user's reset
    /// day, changing it does not wipe the current period.
    /// </summary>
    public async Task<bool> SetNodeTrafficResetDayAsync(Guid nodeId, int? day, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE nodes SET traffic_reset_day=@day, traffic_reset_at_utc=COALESCE(traffic_reset_at_utc,@now) WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", nodeId.ToString("D"));
        cmd.Parameters.AddWithValue("@day", day is null ? DBNull.Value : day.Value);
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Clears the node's traffic counter now. Users' usage and limits are not touched.</summary>
    public async Task<bool> ResetNodeTrafficAsync(Guid nodeId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        return await ResetNodeTrafficAsync(c, nodeId, timeProvider.GetUtcNow(), ct);
    }

    /// <summary>Clears the counter of every node whose monthly reset boundary (00:00 UTC) passed since its last reset.</summary>
    public async Task<int> ResetDueNodeTrafficAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var due = new List<Guid>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using (var select = c.CreateCommand())
        {
            select.CommandText =
                "SELECT id,traffic_reset_day,COALESCE(traffic_reset_at_utc,created_at_utc) FROM nodes WHERE traffic_reset_day IS NOT NULL;";
            await using var r = await select.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                if (SqliteValue.ToDateTimeOffset(r.GetString(2)) < LastTrafficResetBoundary(now, r.GetInt32(1)))
                    due.Add(SqliteValue.ToGuid(r.GetString(0)));
        }

        foreach (var nodeId in due) await ResetNodeTrafficAsync(c, nodeId, now, ct);
        return due.Count;
    }

    private static async Task<bool> ResetNodeTrafficAsync(Microsoft.Data.Sqlite.SqliteConnection c, Guid nodeId,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText =
            "UPDATE nodes SET traffic_upload_bytes=0, traffic_download_bytes=0, traffic_reset_at_utc=@now WHERE id=@id;";
        cmd.Parameters.AddWithValue("@id", nodeId.ToString("D"));
        cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
