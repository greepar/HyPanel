using HyPanel.Server.Security;
using Microsoft.Data.Sqlite;

namespace HyPanel.Server.Persistence;

/// <summary>An ACME certificate as the issuer sees it, secrets decrypted.</summary>
internal sealed record AcmeCertificateWork(Guid Id, string Name, string Domain, string Email, string Challenge,
    string? DnsToken, bool Issued, DateTimeOffset? ExpiresAtUtc, string? Status, DateTimeOffset? AttemptedAtUtc,
    string? AccountUrl, string? AccountKeyPem);

internal sealed partial class SqliteServerRepository
{
    public async Task<IReadOnlyList<AcmeCertificateWork>> GetAcmeCertificatesAsync(CancellationToken ct)
    {
        var result = new List<AcmeCertificateWork>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          SELECT id,name,san,acme_email,acme_challenge,acme_token_nonce,acme_token_ciphertext,acme_token_tag,
                                 certificate_pem IS NOT NULL,expires_at_utc,acme_status,acme_attempted_at_utc,acme_account_url,
                                 acme_account_nonce,acme_account_ciphertext,acme_account_tag
                          FROM certificates WHERE kind='Acme' ORDER BY normalized_name,id;
                          """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var id = SqliteValue.ToGuid(r.GetString(0));
            result.Add(new AcmeCertificateWork(id, r.GetString(1), AcmeDomain(r.GetString(2)) ?? "",
                r.IsDBNull(3) ? "" : r.GetString(3), r.IsDBNull(4) ? "" : r.GetString(4),
                r.IsDBNull(5) ? null : credentialProtector.UnprotectAcmeDnsToken(id,
                    new ProtectedCredential((byte[])r[5], (byte[])r[6], (byte[])r[7])),
                r.GetInt64(8) != 0, r.IsDBNull(9) ? null : SqliteValue.ToDateTimeOffset(r.GetString(9)),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.IsDBNull(11) ? null : SqliteValue.ToDateTimeOffset(r.GetString(11)),
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : credentialProtector.UnprotectAcmeAccountKey(id,
                    new ProtectedCredential((byte[])r[13], (byte[])r[14], (byte[])r[15]))));
        }
        return result;
    }

    /// <summary>Nodes of the enabled services using the certificate: they prove http-01 and tls-alpn-01 challenges.</summary>
    public async Task<IReadOnlyList<(Guid NodeId, string NodeName)>> GetCertificateNodesAsync(Guid certificateId,
        CancellationToken ct)
    {
        var nodes = new List<(Guid, string)>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
                           SELECT DISTINCT n.id,n.display_name FROM service_instances s JOIN nodes n ON n.id=s.node_id
                           WHERE s.enabled=1 AND s.backend_type IN {CertificateBackends}
                             AND json_extract(s.config_json,'$.certificateId')=@id
                           ORDER BY n.display_name COLLATE NOCASE;
                           """;
        cmd.Parameters.AddWithValue("@id", certificateId.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) nodes.Add((SqliteValue.ToGuid(r.GetString(0)), r.GetString(1)));
        return nodes;
    }

    public async Task SetAcmeStateAsync(Guid id, string status, string? error, DateTimeOffset? attemptedAt,
        CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          UPDATE certificates SET acme_status=@status,acme_error=@error,
                            acme_attempted_at_utc=COALESCE(@attempted,acme_attempted_at_utc)
                          WHERE id=@id AND kind='Acme';
                          """;
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@error", error is null ? DBNull.Value : error);
        cmd.Parameters.AddWithValue("@attempted", attemptedAt is null ? DBNull.Value : SqliteValue.ToUtcText(attemptedAt.Value));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>An issuance interrupted by a restart is retried rather than shown as running forever.</summary>
    public async Task ResetInterruptedAcmeAsync(CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE certificates SET acme_status='Pending' WHERE kind='Acme' AND acme_status='Issuing';";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAcmeAccountAsync(Guid id, string url, string keyPem, CancellationToken ct)
    {
        var key = credentialProtector.ProtectAcmeAccountKey(id, keyPem);
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
                          UPDATE certificates SET acme_account_url=@url,acme_account_nonce=@nonce,
                            acme_account_ciphertext=@cipher,acme_account_tag=@tag WHERE id=@id;
                          """;
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        cmd.Parameters.AddWithValue("@url", url);
        cmd.Parameters.AddWithValue("@nonce", key.Nonce);
        cmd.Parameters.AddWithValue("@cipher", key.Ciphertext);
        cmd.Parameters.AddWithValue("@tag", key.Tag);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Stores a newly issued certificate and makes the nodes using it pick it up.</summary>
    public async Task SaveAcmeIssuedAsync(Guid id, string certificatePem, string privateKeyPem, DateTimeOffset notBefore,
        DateTimeOffset notAfter, string fingerprint, string subject, DateTimeOffset now, CancellationToken ct)
    {
        var key = credentialProtector.ProtectCertificateKey(id, privateKeyPem);
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        await using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                              UPDATE certificates SET certificate_pem=@pem,key_nonce=@nonce,key_ciphertext=@cipher,key_tag=@tag,
                                not_before_utc=@before,expires_at_utc=@after,fingerprint=@fingerprint,subject=@subject,
                                acme_status='Issued',acme_error=NULL,acme_attempted_at_utc=@now
                              WHERE id=@id AND kind='Acme';
                              """;
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            cmd.Parameters.AddWithValue("@pem", certificatePem);
            cmd.Parameters.AddWithValue("@nonce", key.Nonce);
            cmd.Parameters.AddWithValue("@cipher", key.Ciphertext);
            cmd.Parameters.AddWithValue("@tag", key.Tag);
            cmd.Parameters.AddWithValue("@before", SqliteValue.ToUtcText(notBefore));
            cmd.Parameters.AddWithValue("@after", SqliteValue.ToUtcText(notAfter));
            cmd.Parameters.AddWithValue("@fingerprint", fingerprint);
            cmd.Parameters.AddWithValue("@subject", subject);
            cmd.Parameters.AddWithValue("@now", SqliteValue.ToUtcText(now));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var revisions = c.CreateCommand())
        {
            revisions.Transaction = tx;
            revisions.CommandText = $"""
                                     UPDATE nodes SET desired_revision=desired_revision+1 WHERE id IN
                                       (SELECT DISTINCT node_id FROM service_instances WHERE backend_type IN {CertificateBackends}
                                        AND json_extract(config_json,'$.certificateId')=@id);
                                     """;
            revisions.Parameters.AddWithValue("@id", id.ToString("D"));
            await revisions.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Makes the nodes sync now (their watch wakes on a revision change), e.g. to fetch a challenge.</summary>
    public async Task WakeNodesAsync(IEnumerable<Guid> nodeIds, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        foreach (var nodeId in nodeIds)
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE nodes SET desired_revision=desired_revision+1 WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", nodeId.ToString("D"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
