namespace HyPanel.Server.Persistence;

using System.Text.Json;
using HyPanel.Shared.Contracts;

internal sealed partial class SqliteServerRepository
{
    /// <summary>
    /// The relay of a service config: <c>relayNodeId</c> names the node whose Agent forwards <c>relayPort</c> to the
    /// service. Null when the service is reached directly.
    /// </summary>
    internal static (Guid NodeId, int Port)? Relay(string configJson)
    {
        try
        {
            using var json = JsonDocument.Parse(configJson);
            var root = json.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("relayNodeId", out var node) && node.ValueKind == JsonValueKind.String
                && node.TryGetGuid(out var id)
                && root.TryGetProperty("relayPort", out var port) && port.TryGetInt32(out var number)
                ? (id, number) : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Forwards this node performs: enabled services elsewhere whose config names it as their relay.</summary>
    public async Task<IReadOnlyList<PortRelayRule>> GetPortRelaysAsync(Guid nodeId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,CAST(json_extract(s.config_json,'$.relayPort') AS INTEGER),a.public_ipv4,
                CAST(json_extract(s.config_json,'$.listenPort') AS INTEGER)
            FROM service_instances s LEFT JOIN agents a ON a.node_id=s.node_id
            WHERE s.enabled=1 AND s.node_id<>@node
                AND lower(json_extract(s.config_json,'$.relayNodeId'))=@node;
            """;
        command.Parameters.AddWithValue("@node", nodeId.ToString("D").ToLowerInvariant());
        var rules = new List<PortRelayRule>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3)) continue;
            var listen = reader.GetInt32(1);
            var target = reader.GetInt32(3);
            if (listen is < 1 or > 65535 || target is < 1 or > 65535) continue;
            rules.Add(new PortRelayRule(SqliteValue.ToGuid(reader.GetString(0)), listen, reader.GetString(2), target));
        }
        return rules;
    }

    /// <summary>
    /// True when <paramref name="port"/> on the relay node collides with a service listening there or with another
    /// service relayed through the same node. <paramref name="serviceId"/> is the service being saved.
    /// </summary>
    public async Task<bool> IsRelayPortTakenAsync(Guid relayNodeId, int port, Guid serviceId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM service_instances s WHERE s.id<>@service AND (
                (s.node_id=@node AND CAST(json_extract(s.config_json,'$.listenPort') AS INTEGER)=@port)
                OR (lower(json_extract(s.config_json,'$.relayNodeId'))=@node
                    AND CAST(json_extract(s.config_json,'$.relayPort') AS INTEGER)=@port))
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@node", relayNodeId.ToString("D").ToLowerInvariant());
        command.Parameters.AddWithValue("@port", port);
        command.Parameters.AddWithValue("@service", serviceId.ToString("D"));
        return await command.ExecuteScalarAsync(ct) is not null;
    }
}
