namespace HyPanel.Shared.Networking;

/// <summary>
/// A set of ports written as comma-separated single ports and ranges, e.g. <c>20000-30000</c> or
/// <c>443,8443,20000-20100</c>. Used for Hysteria2 port hopping by the Server, the Agent and subscriptions.
/// </summary>
public sealed record PortSpec(IReadOnlyList<PortRange> Ranges)
{
    public const int MaximumSegments = 32;

    public int Count => Ranges.Sum(range => range.To - range.From + 1);

    /// <summary>Canonical text form: sorted, merged, no spaces (e.g. <c>443,20000-30000</c>).</summary>
    public override string ToString() =>
        string.Join(',', Ranges.Select(range => range.From == range.To ? $"{range.From}" : $"{range.From}-{range.To}"));

    public static bool TryParse(string? text, out PortSpec spec)
    {
        spec = new PortSpec([]);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var ranges = new List<PortRange>();
        foreach (var raw in text.Replace('，', ',').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = raw.Replace('–', '-').Replace(':', '-').Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 2 || !TryPort(parts[0], out var from)) return false;
            var to = from;
            if (parts.Length == 2 && !TryPort(parts[1], out to)) return false;
            if (to < from) return false;
            ranges.Add(new PortRange(from, to));
        }
        if (ranges.Count is 0 or > MaximumSegments) return false;
        var merged = new List<PortRange>();
        foreach (var range in ranges.OrderBy(range => range.From))
        {
            if (merged.Count > 0 && range.From <= merged[^1].To + 1)
                merged[^1] = merged[^1] with { To = Math.Max(merged[^1].To, range.To) };
            else merged.Add(range);
        }
        spec = new PortSpec(merged);
        return true;
    }

    public bool Contains(int port) => Ranges.Any(range => port >= range.From && port <= range.To);

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)
        && port is >= 1 and <= 65_535;
}

public readonly record struct PortRange(int From, int To);
