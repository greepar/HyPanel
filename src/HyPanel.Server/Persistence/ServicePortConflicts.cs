namespace HyPanel.Server.Persistence;

using System.Text.Json;

/// <summary>What the port-conflict check needs of a service, so it runs over any mix of stored and proposed ones.</summary>
internal sealed record ServiceBindingSource(Guid Id, Guid NodeId, string Name, string BackendType, bool Enabled,
    string ConfigJson);

/// <summary>
/// Two enabled services cannot listen on the same port and protocol of one node, and a relay port is a TCP listener on
/// the relay node. Disabled services hold no port, so they never conflict.
/// </summary>
internal static class ServicePortConflicts
{
    [Flags]
    private enum Transport { Tcp = 1, Udp = 2 }

    private readonly record struct Bind(Guid NodeId, string Host, int Port, Transport Transport, string Description);

    /// <summary>The first conflict between <paramref name="candidate"/> and the others, or null.</summary>
    internal static string? Find(ServiceBindingSource candidate, IEnumerable<ServiceBindingSource> others)
    {
        if (!candidate.Enabled) return null;
        var mine = Binds(candidate).ToArray();
        if (mine.Length == 0) return null;
        foreach (var other in others)
        {
            if (other.Id == candidate.Id || !other.Enabled) continue;
            foreach (var theirs in Binds(other))
            foreach (var ours in mine)
                if (ours.NodeId == theirs.NodeId && ours.Port == theirs.Port && (ours.Transport & theirs.Transport) != 0
                    && Overlaps(ours.Host, theirs.Host))
                    return $"{ours.Description}与{theirs.Description}冲突，请换一个端口。";
        }
        return null;
    }

    private static IEnumerable<Bind> Binds(ServiceBindingSource service)
    {
        JsonDocument json;
        try { json = JsonDocument.Parse(service.ConfigJson); }
        catch (JsonException) { yield break; }
        using (json)
        {
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) yield break;
            if (root.TryGetProperty("listenPort", out var port) && port.TryGetInt32(out var listen) && listen is >= 1 and <= 65535)
            {
                var host = root.TryGetProperty("listenHost", out var h) && h.ValueKind == JsonValueKind.String
                    ? h.GetString() ?? "" : "";
                var transport = service.BackendType switch
                {
                    "hysteria2" => Transport.Udp,
                    "xray" => Transport.Tcp,
                    "xray-ss" => Transport.Tcp | (root.TryGetProperty("udp", out var u) && u.ValueKind == JsonValueKind.True
                        ? Transport.Udp : 0),
                    _ => Transport.Tcp
                };
                yield return new(service.NodeId, host, listen, transport, $"服务“{service.Name}”的监听端口 {listen}");
            }
        }
        if (SqliteServerRepository.Relay(service.ConfigJson) is { } relay && relay.Port is >= 1 and <= 65535)
            yield return new(relay.NodeId, "", relay.Port, Transport.Tcp, $"服务“{service.Name}”的转发端口 {relay.Port}");
    }

    private static bool Overlaps(string a, string b) => IsWildcard(a) || IsWildcard(b) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsWildcard(string host) => host is "" or "0.0.0.0" or "::" or "[::]" or "*";
}
