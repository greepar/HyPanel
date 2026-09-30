namespace HyPanel.Server.Persistence;

using System.Text.Json;
using System.Security.Cryptography;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using HyPanel.Server.Backends;
using Microsoft.Data.Sqlite;

internal sealed record EgressNodeRecord(Guid Id, string DisplayName, string? PublicIpv4,
    bool Enabled, bool Ready, string? Error, int UsedBy, bool Supported, string Transport, IReadOnlyList<string> SupportedTransports, int UdpPort);

internal sealed partial class SqliteServerRepository
{
    internal static Guid? ExitNode(string configJson)
    {
        try
        {
            using var json = JsonDocument.Parse(configJson);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("exitNodeId", out var value)
                && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) ? id : null;
        }
        catch (JsonException) { return null; }
    }

    public async Task<IReadOnlyList<EgressNodeRecord>> GetEgressNodesAsync(CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT n.id,n.display_name,a.public_ipv4,COALESCE(e.enabled,0),
                COALESCE(e.ready,0),e.error,a.last_seen_at_utc,e.observed_at_utc,
                (SELECT COUNT(*) FROM service_egress s WHERE s.exit_node_id=n.id),
                COALESCE(e.transport,'gre'),e.supported_transports_json,COALESCE(e.udp_port,0)
            FROM nodes n LEFT JOIN agents a ON a.node_id=n.id LEFT JOIN node_egress e ON e.node_id=n.id;
            """;
        var nodes = new List<EgressNodeRecord>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        var cutoff = timeProvider.GetUtcNow() - TimeSpan.FromSeconds(30);
        while (await reader.ReadAsync(ct))
            nodes.Add(new EgressNodeRecord(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetBoolean(3),
                reader.GetBoolean(4) && !reader.IsDBNull(6) && !reader.IsDBNull(7)
                    && SqliteValue.ToDateTimeOffset(reader.GetString(6)) >= cutoff
                    && SqliteValue.ToDateTimeOffset(reader.GetString(7)) >= cutoff,
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(8), !reader.IsDBNull(7) && SqliteValue.ToDateTimeOffset(reader.GetString(7)) >= cutoff,
                reader.GetString(9), ReadSupportedTransports(reader.IsDBNull(10) ? null : reader.GetString(10)), reader.GetInt32(11)));
        return nodes;
    }

    public async Task<IReadOnlyList<int>> GetEgressUdpPortsAsync(Guid nodeId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(NULLIF(udp_port,0),CASE WHEN transport='wireguard' THEN 51820 ELSE 47541 END)
            FROM node_egress WHERE enabled=1 AND node_id=@node AND transport IN ('gre-udp','wireguard')
            UNION
            SELECT COALESCE(NULLIF(x.udp_port,0),47541)
            FROM service_egress e JOIN service_instances s ON s.id=e.service_id
                JOIN node_egress x ON x.node_id=e.exit_node_id
            WHERE s.node_id=@node AND x.transport='gre-udp';
            """;
        command.Parameters.AddWithValue("@node", nodeId.ToString("D"));
        var ports = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ports.Add(reader.GetInt32(0));
        return ports;
    }

    public async Task<bool> SetEgressEnabledAsync(Guid nodeId, bool enabled, CancellationToken ct, string transport = EgressTransports.Gre, int udpPort = 0)
    {
        if (transport != EgressTransports.Gre && udpPort == 0)
            udpPort = transport == EgressTransports.WireGuard ? EgressTransports.WireGuardPort : EgressTransports.GreUdpPort;
        if (transport != EgressTransports.Gre && udpPort is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(udpPort));
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO node_egress(node_id,enabled,ready,transport,udp_port,error,observed_at_utc)
            SELECT @node,@enabled,0,@transport,@port,NULL,NULL WHERE EXISTS(SELECT 1 FROM nodes WHERE id=@node)
                AND (@enabled=1 OR NOT EXISTS(SELECT 1 FROM service_egress WHERE exit_node_id=@node))
                AND (NOT EXISTS(SELECT 1 FROM service_egress WHERE exit_node_id=@node)
                    OR EXISTS(SELECT 1 FROM node_egress WHERE node_id=@node AND transport=@transport
                        AND COALESCE(NULLIF(udp_port,0),CASE WHEN transport='wireguard' THEN 51820 WHEN transport='gre-udp' THEN 47541 ELSE 0 END)=@port))
            ON CONFLICT(node_id) DO UPDATE SET enabled=excluded.enabled,transport=excluded.transport,udp_port=excluded.udp_port,ready=0,error=NULL,observed_at_utc=NULL;
            """;
        command.Parameters.AddWithValue("@node", nodeId.ToString("D"));
        command.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("@transport", transport);
        command.Parameters.AddWithValue("@port", udpPort);
        if (await command.ExecuteNonQueryAsync(ct) != 1) return false;
        await IncrementRevisionAsync(connection, transaction, nodeId, ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task RecordEgressReportAsync(Guid nodeId, EgressNetworkReport? report, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        if (report is null)
        {
            command.CommandText = "UPDATE node_egress SET ready=0,error=NULL,observed_at_utc=NULL,supported_transports_json=NULL WHERE node_id=@node;";
            command.Parameters.AddWithValue("@node", nodeId.ToString("D"));
            await command.ExecuteNonQueryAsync(ct);
            return;
        }
        command.CommandText = """
            INSERT INTO node_egress(node_id,enabled,ready) SELECT @node,0,0 WHERE @enabled=0
                ON CONFLICT(node_id) DO NOTHING;
            UPDATE node_egress SET ready=@ready,error=@error,observed_at_utc=@now,supported_transports_json=@supported
            WHERE node_id=@node AND enabled=@enabled AND transport=@transport
                AND (udp_port=@port OR @port=0 AND (transport='gre' OR udp_port=0 OR transport='gre-udp' AND udp_port=47541));
            """;
        command.Parameters.AddWithValue("@node", nodeId.ToString("D"));
        command.Parameters.AddWithValue("@enabled", report.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@transport", report.AppliedTransport);
        command.Parameters.AddWithValue("@port", report.AppliedUdpPort);
        command.Parameters.AddWithValue("@ready", report.Ready ? 1 : 0);
        command.Parameters.AddWithValue("@supported", JsonSerializer.Serialize(
            (report.SupportedTransports ?? [EgressTransports.Gre]).ToArray(), HyPanelJsonSerializerContext.Default.StringArray));
        command.Parameters.AddWithValue("@error", (object?)report.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(timeProvider.GetUtcNow()));
        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<EgressNetworkState> GetEgressNetworkAsync(Guid nodeId, CancellationToken ct)
    {
        await using var connection = await connectionFactory.OpenAsync(ct);
        await using var enabled = connection.CreateCommand();
        enabled.CommandText = "SELECT enabled,transport,udp_port FROM node_egress WHERE node_id=@node";
        enabled.Parameters.AddWithValue("@node", nodeId.ToString("D"));
        var isEnabled = false;
        var transport = EgressTransports.Gre;
        var udpPort = 0;
        await using (var row = await enabled.ExecuteReaderAsync(ct))
            if (await row.ReadAsync(ct)) { isEnabled = row.GetBoolean(0); transport = row.GetString(1); udpPort = row.GetInt32(2); }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.service_id,e.slot,s.node_id,e.exit_node_id,
                CASE WHEN s.node_id=@node THEN b.public_ipv4 ELSE a.public_ipv4 END, x.transport,x.udp_port
            FROM service_egress e JOIN service_instances s ON s.id=e.service_id
                LEFT JOIN agents a ON a.node_id=s.node_id LEFT JOIN agents b ON b.node_id=e.exit_node_id
                JOIN node_egress x ON x.node_id=e.exit_node_id
            WHERE s.node_id=@node OR e.exit_node_id=@node;
            """;
        command.Parameters.AddWithValue("@node", nodeId.ToString("D"));
        var tunnels = new List<EgressTunnel>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sourceNode = Guid.Parse(reader.GetString(2));
            var exitNode = Guid.Parse(reader.GetString(3));
            var isExit = exitNode == nodeId;
            var tunnelTransport = reader.GetString(5);
            var port = reader.GetInt32(6);
            if (port == 0) port = tunnelTransport == EgressTransports.WireGuard ? EgressTransports.WireGuardPort : EgressTransports.GreUdpPort;
            var options = tunnelTransport switch
            {
                EgressTransports.GreUdp when port != EgressTransports.GreUdpPort => JsonSerializer.Serialize(new EgressUdpOptions(port), HyPanelJsonSerializerContext.Default.EgressUdpOptions),
                EgressTransports.WireGuard => WireGuardOptions(sourceNode, exitNode, isExit, port),
                _ => null
            };
            // Missing addresses remain explicit failures, never silently become a local outbound.
            tunnels.Add(new EgressTunnel(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), isExit,
                reader.IsDBNull(4) ? "" : reader.GetString(4), tunnelTransport, options));
        }
        if (udpPort == 0) udpPort = transport == EgressTransports.WireGuard ? EgressTransports.WireGuardPort : EgressTransports.GreUdpPort;
        var stateOptions = transport switch
        {
            EgressTransports.GreUdp when udpPort != EgressTransports.GreUdpPort => JsonSerializer.Serialize(new EgressUdpOptions(udpPort), HyPanelJsonSerializerContext.Default.EgressUdpOptions),
            EgressTransports.WireGuard when isEnabled => WireGuardOptions(nodeId, nodeId, true, udpPort),
            _ => null
        };
        return new EgressNetworkState(isEnabled, tunnels, transport, stateOptions);
    }

    private string WireGuardOptions(Guid sourceNode, Guid exitNode, bool isExit, int port)
    {
        var own = credentialProtector.DeriveWireGuardPrivateKey(isExit ? exitNode : sourceNode, isExit ? null : exitNode);
        var other = sourceNode == exitNode ? null : credentialProtector.DeriveWireGuardPrivateKey(isExit ? sourceNode : exitNode, isExit ? exitNode : null);
        try
        {
            var publicKey = new byte[32];
            if (other is not null) Org.BouncyCastle.Math.EC.Rfc7748.X25519.GeneratePublicKey(other, 0, publicKey, 0);
            var value = new WireGuardEgressOptions(port, isExit ? "hpwx" : $"hpw{exitNode:N}"[..15],
                Convert.ToBase64String(own), other is null ? null : Convert.ToBase64String(publicKey));
            return JsonSerializer.Serialize(value, HyPanelJsonSerializerContext.Default.WireGuardEgressOptions);
        }
        finally { CryptographicOperations.ZeroMemory(own); if (other is not null) CryptographicOperations.ZeroMemory(other); }
    }

    private static IReadOnlyList<string> ReadSupportedTransports(string? json)
    {
        if (json is null) return [];
        try { return JsonSerializer.Deserialize(json, HyPanelJsonSerializerContext.Default.StringArray) ?? []; }
        catch (JsonException) { return []; }
    }

    private static async Task UpdateServiceEgressAsync(SqliteConnection connection, SqliteTransaction transaction,
        ServiceInstanceRecord service, CancellationToken ct)
    {
        var exit = ExitNode(service.ConfigJson);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@service", service.Id.ToString("D"));
        if (exit is null)
            command.CommandText = "DELETE FROM service_egress WHERE service_id=@service;";
        else
        {
            if (!BackendDefinitionCatalog.TryGet(service.BackendType, out var definition)
                || !definition!.SupportsEgress || exit == service.NodeId)
                throw new InvalidOperationException("Invalid exit node.");
            command.Parameters.AddWithValue("@exit", exit.Value.ToString("D"));
            // Recheck within the transaction so disabling an exit cannot race a service save.
            command.CommandText = "SELECT 1 FROM node_egress WHERE node_id=@exit AND enabled=1;";
            if (await command.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Exit node is not ready.");
            command.CommandText = """
                INSERT INTO service_egress(slot,service_id,exit_node_id)
                VALUES(COALESCE((SELECT slot FROM service_egress WHERE service_id=@service),
                    (SELECT COALESCE(MAX(slot),0)+1 FROM service_egress)),@service,@exit)
                ON CONFLICT(service_id) DO UPDATE SET exit_node_id=excluded.exit_node_id;
                """;
        }
        await command.ExecuteNonQueryAsync(ct);
    }
}
