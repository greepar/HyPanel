namespace HyPanel.Agent.Backends.Mihomo;

/// <summary>
/// Turns Mihomo's connection list into cumulative per-user counters. Mihomo only lists open connections, so bytes of a
/// connection that opened or closed between two polls never show up per user; they do show up in the process totals.
/// Each poll adds every open connection's growth to its user, then hands the rest of the process totals' growth to the
/// users whose traffic it most likely was (those whose connections just closed, else those active in this poll, else
/// by their totals so far). The node total is exact; with several users their split is close but not exact.
/// </summary>
internal sealed class MihomoTrafficTracker
{
    private readonly Dictionary<string, (Guid User, long Upload, long Download)> open = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, (long Upload, long Download)> totals = [];
    private long processUpload;
    private long processDownload;

    /// <summary>The users' cumulative counters so far.</summary>
    public IReadOnlyDictionary<Guid, (long Upload, long Download)> Totals => totals;

    /// <summary>The users' cumulative counters after taking in one <c>/connections</c> snapshot.</summary>
    public IReadOnlyDictionary<Guid, (long Upload, long Download)> Observe(MihomoConnections snapshot,
        Func<string?, Guid?> userOf)
    {
        if (snapshot.UploadTotal < processUpload || snapshot.DownloadTotal < processDownload)
        {
            // Mihomo restarted: its counters and connection ids start over; what was counted stays counted.
            open.Clear();
            processUpload = 0;
            processDownload = 0;
        }

        var seen = new Dictionary<Guid, (long Upload, long Download)>();
        var current = new Dictionary<string, (Guid User, long Upload, long Download)>(StringComparer.Ordinal);
        foreach (var connection in snapshot.Connections ?? [])
        {
            if (connection.Id is null || userOf(connection.Metadata?.InboundUser) is not { } user
                || connection.Upload < 0 || connection.Download < 0) continue;
            open.TryGetValue(connection.Id, out var before);
            var upload = Math.Max(0, connection.Upload - before.Upload);
            var download = Math.Max(0, connection.Download - before.Download);
            current[connection.Id] = (user, connection.Upload, connection.Download);
            Add(seen, user, upload, download);
        }

        // Connections that closed since the last poll: their last bytes are in the process totals only.
        var closed = new Dictionary<Guid, (long Upload, long Download)>();
        foreach (var (id, previous) in open)
            if (!current.ContainsKey(id)) Add(closed, previous.User, Math.Max(1, previous.Upload), Math.Max(1, previous.Download));

        var restUpload = Math.Max(0, snapshot.UploadTotal - processUpload - seen.Values.Sum(item => item.Upload));
        var restDownload = Math.Max(0, snapshot.DownloadTotal - processDownload - seen.Values.Sum(item => item.Download));
        foreach (var (user, (upload, download)) in seen) Add(totals, user, upload, download);
        Share(restUpload, closed.Count > 0 ? closed : seen.Count > 0 ? seen : totals, upload: true);
        Share(restDownload, closed.Count > 0 ? closed : seen.Count > 0 ? seen : totals, upload: false);

        open.Clear();
        foreach (var (id, value) in current) open[id] = value;
        processUpload = snapshot.UploadTotal;
        processDownload = snapshot.DownloadTotal;
        return totals;
    }

    /// <summary>Drops users no longer granted, so a returning user starts again from zero like on other engines.</summary>
    public void Retain(IReadOnlySet<Guid> users)
    {
        foreach (var user in totals.Keys.Where(user => !users.Contains(user)).ToArray()) totals.Remove(user);
    }

    private void Share(long bytes, Dictionary<Guid, (long Upload, long Download)> weights, bool upload)
    {
        if (bytes <= 0 || weights.Count == 0) return;
        var weighted = weights.Select(pair => (pair.Key, Weight: upload ? pair.Value.Upload : pair.Value.Download))
            .OrderBy(pair => pair.Key).ToArray();
        var sum = weighted.Sum(pair => pair.Weight);
        if (sum <= 0) weighted = [.. weighted.Select(pair => (pair.Key, Weight: 1L))];
        sum = weighted.Sum(pair => pair.Weight);
        var left = bytes;
        for (var index = 0; index < weighted.Length; index++)
        {
            var part = index == weighted.Length - 1 ? left : (long)((decimal)bytes * weighted[index].Weight / sum);
            left -= part;
            Add(totals, weighted[index].Key, upload ? part : 0, upload ? 0 : part);
        }
    }

    private static void Add(Dictionary<Guid, (long Upload, long Download)> into, Guid user, long upload, long download)
    {
        into.TryGetValue(user, out var value);
        into[user] = (value.Upload + upload, value.Download + download);
    }
}
