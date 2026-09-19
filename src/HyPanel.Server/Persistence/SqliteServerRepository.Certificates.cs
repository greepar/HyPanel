namespace HyPanel.Server.Persistence;

using HyPanel.Server.Security;
using Microsoft.Data.Sqlite;

internal sealed partial class SqliteServerRepository
{
    public async Task<IReadOnlyList<CertificateRecord>> GetCertificatesAsync(CancellationToken ct)
    {
        var result = new List<CertificateRecord>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT c.id,c.name,c.certificate_pem,c.created_at_utc,c.not_before_utc,c.expires_at_utc,c.fingerprint,c.subject,c.san,
              (SELECT COUNT(*) FROM service_instances s WHERE s.backend_type='hysteria2' AND json_extract(s.config_json,'$.certificateId')=c.id)
            FROM certificates c ORDER BY c.normalized_name,c.id;
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(ReadCertificate(r));
        return result;
    }

    public async Task<CertificateRecord?> GetCertificateWithKeyAsync(Guid id, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id,name,certificate_pem,created_at_utc,not_before_utc,expires_at_utc,fingerprint,subject,san,
              (SELECT COUNT(*) FROM service_instances s WHERE s.backend_type='hysteria2' AND json_extract(s.config_json,'$.certificateId')=certificates.id),
              key_nonce,key_ciphertext,key_tag FROM certificates WHERE id=@id;
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var value = ReadCertificate(r);
        var key = credentialProtector.UnprotectCertificateKey(id,
            new ProtectedCredential((byte[])r[10], (byte[])r[11], (byte[])r[12]));
        return value with { PrivateKeyPem = key };
    }

    public async Task<bool> CreateCertificateAsync(CertificateRecord value, CancellationToken ct) =>
        await SaveCertificateAsync(value, false, ct);

    public async Task<bool> ReplaceCertificateAsync(CertificateRecord value, CancellationToken ct) =>
        await SaveCertificateAsync(value, true, ct);

    public async Task InitializeCertificatesAsync(CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM certificates);";
        if ((long)(await cmd.ExecuteScalarAsync(ct) ?? 0L) != 0 && !credentialProtector.IsConfigured)
            throw new InvalidOperationException(
                "HyPanel:Security:MasterKey is required because encrypted certificate keys already exist.");
    }

    private async Task<bool> SaveCertificateAsync(CertificateRecord value, bool replace, CancellationToken ct)
    {
        var protectedKey = credentialProtector.ProtectCertificateKey(value.Id, value.PrivateKeyPem!);
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = replace ? """
                UPDATE certificates SET name=@name,normalized_name=@normalized,certificate_pem=@pem,key_nonce=@nonce,
                  key_ciphertext=@cipher,key_tag=@tag,not_before_utc=@before,expires_at_utc=@expires,
                  fingerprint=@fingerprint,subject=@subject,san=@san WHERE id=@id;
                """ : """
                INSERT INTO certificates (id,name,normalized_name,certificate_pem,key_nonce,key_ciphertext,key_tag,
                  created_at_utc,not_before_utc,expires_at_utc,fingerprint,subject,san)
                VALUES (@id,@name,@normalized,@pem,@nonce,@cipher,@tag,@created,@before,@expires,@fingerprint,@subject,@san);
                """;
            AddCertificateParameters(cmd, value, protectedKey);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) { await tx.RollbackAsync(ct); return false; }
            if (replace)
            {
                await using var revisions = c.CreateCommand();
                revisions.Transaction = tx;
                revisions.CommandText = """
                    UPDATE nodes SET desired_revision=desired_revision+1 WHERE id IN
                      (SELECT DISTINCT node_id FROM service_instances WHERE backend_type='hysteria2'
                       AND json_extract(config_json,'$.certificateId')=@id);
                    """;
                revisions.Parameters.AddWithValue("@id", value.Id.ToString("D"));
                await revisions.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            return true;
        }
        catch (SqliteException e) when (e.SqliteErrorCode == 19) { await tx.RollbackAsync(CancellationToken.None); return false; }
    }

    private static CertificateRecord ReadCertificate(SqliteDataReader r) => new(SqliteValue.ToGuid(r.GetString(0)),
        r.GetString(1), r.GetString(2), SqliteValue.ToDateTimeOffset(r.GetString(3)),
        SqliteValue.ToDateTimeOffset(r.GetString(4)), SqliteValue.ToDateTimeOffset(r.GetString(5)), r.GetString(6),
        r.GetString(7), r.GetString(8), r.GetInt32(9));

    private static void AddCertificateParameters(SqliteCommand cmd, CertificateRecord value, ProtectedCredential key)
    {
        cmd.Parameters.AddWithValue("@id", value.Id.ToString("D"));
        cmd.Parameters.AddWithValue("@name", value.Name);
        cmd.Parameters.AddWithValue("@normalized", value.Name.ToUpperInvariant());
        cmd.Parameters.AddWithValue("@pem", value.CertificatePem);
        cmd.Parameters.AddWithValue("@nonce", key.Nonce); cmd.Parameters.AddWithValue("@cipher", key.Ciphertext);
        cmd.Parameters.AddWithValue("@tag", key.Tag);
        cmd.Parameters.AddWithValue("@created", SqliteValue.ToUtcText(value.CreatedAtUtc));
        cmd.Parameters.AddWithValue("@before", SqliteValue.ToUtcText(value.NotBeforeUtc));
        cmd.Parameters.AddWithValue("@expires", SqliteValue.ToUtcText(value.ExpiresAtUtc));
        cmd.Parameters.AddWithValue("@fingerprint", value.Fingerprint); cmd.Parameters.AddWithValue("@subject", value.Subject);
        cmd.Parameters.AddWithValue("@san", value.San);
    }
}
