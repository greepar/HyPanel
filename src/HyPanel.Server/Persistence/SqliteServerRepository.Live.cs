namespace HyPanel.Server.Persistence;

internal sealed partial class SqliteServerRepository
{
    /// <summary>The Agent version the node last reported; null when it never synced.</summary>
    public async Task<string?> GetNodeAgentVersionAsync(Guid nodeId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT reported_version FROM agents WHERE node_id = @nodeId;";
        cmd.Parameters.AddWithValue("@nodeId", nodeId.ToString("D"));
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>
    /// A short text that changes whenever the Panel has something new for an Agent's next sync: a new desired
    /// revision, a new or finished command, or a requested Agent update.
    /// </summary>
    public async Task<(long Revision, string Fingerprint)> GetAgentWatchStateAsync(Guid agentId, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          SELECT n.desired_revision,
                                 COALESCE(n.agent_update_id, ''),
                                 (SELECT COUNT(*) || ':' || COALESCE(MAX(created_at_utc), '')
                                  FROM agent_commands ac
                                  WHERE ac.agent_id = a.id AND ac.status IN ('Pending', 'Running'))
                          FROM agents a JOIN nodes n ON n.id = a.node_id
                          WHERE a.id = @agentId;
                          """;
        cmd.Parameters.AddWithValue("@agentId", agentId.ToString("D"));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (-1, "gone");
        var revision = reader.GetInt64(0);
        return (revision, $"{revision}|{reader.GetString(1)}|{reader.GetString(2)}");
    }

    /// <summary>
    /// Deletes usage receipts older than <see cref="UsageReceiptRetention"/>; they otherwise grow with every sync that
    /// carries traffic. Returns how many were deleted.
    /// </summary>
    public async Task<int> PruneUsageReceiptsAsync(CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM usage_batches WHERE accepted_at_utc < @cutoff;";
        cmd.Parameters.AddWithValue("@cutoff", SqliteValue.ToUtcText(timeProvider.GetUtcNow() - UsageReceiptRetention));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Rebuilds the database file when at least a quarter of it (and 32 MB) is free pages, so space freed by pruning
    /// goes back to the disk. Returns the bytes reclaimed, or 0 when nothing was done.
    /// </summary>
    public async Task<long> CompactIfWastefulAsync(CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        async Task<long> Pragma(string name)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"PRAGMA {name};";
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        var pageSize = await Pragma("page_size");
        var free = await Pragma("freelist_count");
        var total = await Pragma("page_count");
        if (free * pageSize < 32L * 1024 * 1024 || free * 4 < total) return 0;
        await using (var vacuum = c.CreateCommand())
        {
            vacuum.CommandText = "VACUUM;";
            vacuum.CommandTimeout = 600;
            await vacuum.ExecuteNonQueryAsync(ct);
        }
        return (total - await Pragma("page_count")) * pageSize;
    }
}
