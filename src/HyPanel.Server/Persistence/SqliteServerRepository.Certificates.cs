namespace HyPanel.Server.Persistence;

using HyPanel.Server.Security;
using Microsoft.Data.Sqlite;

internal sealed partial class SqliteServerRepository
{
    private const string CertificateColumns = """
        c.id,c.name,c.kind,c.certificate_pem,c.created_at_utc,c.not_before_utc,c.expires_at_utc,c.fingerprint,c.subject,c.san,
          (SELECT COUNT(*) FROM service_instances s WHERE s.backend_type='hysteria2' AND json_extract(s.config_json,'$.certificateId')=c.id),
          c.certificate_path,c.private_key_path,c.acme_email,c.acme_challenge,c.acme_token_nonce IS NOT NULL
        """;

    public async Task<IReadOnlyList<CertificateRecord>> GetCertificatesAsync(CancellationToken ct)
    {
        var result = new List<CertificateRecord>();
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {CertificateColumns} FROM certificates c ORDER BY c.normalized_name,c.id;";
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(ReadCertificate(r));
        return result;
    }

    /// <summary>Loads a certificate including its decrypted secrets (upload private key, ACME DNS token).</summary>
    public async Task<CertificateRecord?> GetCertificateWithKeyAsync(Guid id, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            SELECT {CertificateColumns},c.key_nonce,c.key_ciphertext,c.key_tag,
              c.acme_token_nonce,c.acme_token_ciphertext,c.acme_token_tag
            FROM certificates c WHERE c.id=@id;
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var value = ReadCertificate(r);
        if (!r.IsDBNull(16))
            value = value with
            {
                PrivateKeyPem = credentialProtector.UnprotectCertificateKey(id,
                    new ProtectedCredential((byte[])r[16], (byte[])r[17], (byte[])r[18]))
            };
        if (!r.IsDBNull(19))
            value = value with
            {
                AcmeDnsToken = credentialProtector.UnprotectAcmeDnsToken(id,
                    new ProtectedCredential((byte[])r[19], (byte[])r[20], (byte[])r[21]))
            };
        return value;
    }

    public async Task<bool> CreateCertificateAsync(CertificateRecord value, CancellationToken ct) =>
        await SaveCertificateAsync(value, false, ct);

    public async Task<bool> ReplaceCertificateAsync(CertificateRecord value, CancellationToken ct) =>
        await SaveCertificateAsync(value, true, ct);

    /// <summary>Deletes a certificate that no service references. Returns null when it is still in use.</summary>
    public async Task<bool?> DeleteCertificateAsync(Guid id, CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT EXISTS(SELECT 1 FROM service_instances WHERE backend_type='hysteria2'
              AND json_extract(config_json,'$.certificateId')=@id);
            """;
        cmd.Parameters.AddWithValue("@id", id.ToString("D"));
        if ((long)(await cmd.ExecuteScalarAsync(ct) ?? 0L) != 0) return null;
        await using var delete = c.CreateCommand();
        delete.CommandText = "DELETE FROM certificates WHERE id=@id;";
        delete.Parameters.AddWithValue("@id", id.ToString("D"));
        return await delete.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task InitializeCertificatesAsync(CancellationToken ct)
    {
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT EXISTS(SELECT 1 FROM certificates WHERE key_nonce IS NOT NULL OR acme_token_nonce IS NOT NULL);";
        if ((long)(await cmd.ExecuteScalarAsync(ct) ?? 0L) != 0 && !credentialProtector.IsConfigured)
            throw new InvalidOperationException(
                "HyPanel:Security:MasterKey is required because encrypted certificate secrets already exist.");
    }

    private async Task<bool> SaveCertificateAsync(CertificateRecord value, bool replace, CancellationToken ct)
    {
        var key = value.PrivateKeyPem is null ? null : credentialProtector.ProtectCertificateKey(value.Id, value.PrivateKeyPem);
        var token = value.AcmeDnsToken is null ? null : credentialProtector.ProtectAcmeDnsToken(value.Id, value.AcmeDnsToken);
        // On replace an omitted ACME token keeps the stored one unless the challenge no longer needs it.
        var keepToken = replace && token is null && value.AcmeChallenge == "cloudflare";
        await using var c = await connectionFactory.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = c.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = replace ? $"""
                UPDATE certificates SET name=@name,normalized_name=@normalized,kind=@kind,certificate_pem=@pem,
                  key_nonce=@nonce,key_ciphertext=@cipher,key_tag=@tag,not_before_utc=@before,expires_at_utc=@expires,
                  fingerprint=@fingerprint,subject=@subject,san=@san,certificate_path=@certPath,private_key_path=@keyPath,
                  acme_email=@email,acme_challenge=@challenge
                  {(keepToken ? "" : ",acme_token_nonce=@tokenNonce,acme_token_ciphertext=@tokenCipher,acme_token_tag=@tokenTag")}
                WHERE id=@id;
                """ : """
                INSERT INTO certificates (id,name,normalized_name,kind,certificate_pem,key_nonce,key_ciphertext,key_tag,
                  created_at_utc,not_before_utc,expires_at_utc,fingerprint,subject,san,certificate_path,private_key_path,
                  acme_email,acme_challenge,acme_token_nonce,acme_token_ciphertext,acme_token_tag)
                VALUES (@id,@name,@normalized,@kind,@pem,@nonce,@cipher,@tag,@created,@before,@expires,@fingerprint,
                  @subject,@san,@certPath,@keyPath,@email,@challenge,@tokenNonce,@tokenCipher,@tokenTag);
                """;
            AddCertificateParameters(cmd, value, key, token);
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
        r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), SqliteValue.ToDateTimeOffset(r.GetString(4)),
        r.IsDBNull(5) ? null : SqliteValue.ToDateTimeOffset(r.GetString(5)),
        r.IsDBNull(6) ? null : SqliteValue.ToDateTimeOffset(r.GetString(6)),
        r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetInt32(10),
        CertificatePath: r.IsDBNull(11) ? null : r.GetString(11), PrivateKeyPath: r.IsDBNull(12) ? null : r.GetString(12),
        AcmeEmail: r.IsDBNull(13) ? null : r.GetString(13), AcmeChallenge: r.IsDBNull(14) ? null : r.GetString(14),
        HasAcmeDnsToken: r.GetInt64(15) != 0);

    private static void AddCertificateParameters(SqliteCommand cmd, CertificateRecord value, ProtectedCredential? key,
        ProtectedCredential? token)
    {
        static object Db(object? item) => item ?? DBNull.Value;
        cmd.Parameters.AddWithValue("@id", value.Id.ToString("D"));
        cmd.Parameters.AddWithValue("@name", value.Name);
        cmd.Parameters.AddWithValue("@normalized", value.Name.ToUpperInvariant());
        cmd.Parameters.AddWithValue("@kind", value.Kind);
        cmd.Parameters.AddWithValue("@pem", Db(value.CertificatePem));
        cmd.Parameters.Add("@nonce", SqliteType.Blob).Value = Db(key?.Nonce);
        cmd.Parameters.Add("@cipher", SqliteType.Blob).Value = Db(key?.Ciphertext);
        cmd.Parameters.Add("@tag", SqliteType.Blob).Value = Db(key?.Tag);
        cmd.Parameters.AddWithValue("@created", SqliteValue.ToUtcText(value.CreatedAtUtc));
        cmd.Parameters.AddWithValue("@before", value.NotBeforeUtc is { } before ? SqliteValue.ToUtcText(before) : DBNull.Value);
        cmd.Parameters.AddWithValue("@expires", value.ExpiresAtUtc is { } expires ? SqliteValue.ToUtcText(expires) : DBNull.Value);
        cmd.Parameters.AddWithValue("@fingerprint", Db(value.Fingerprint));
        cmd.Parameters.AddWithValue("@subject", Db(value.Subject));
        cmd.Parameters.AddWithValue("@san", value.San);
        cmd.Parameters.AddWithValue("@certPath", Db(value.CertificatePath));
        cmd.Parameters.AddWithValue("@keyPath", Db(value.PrivateKeyPath));
        cmd.Parameters.AddWithValue("@email", Db(value.AcmeEmail));
        cmd.Parameters.AddWithValue("@challenge", Db(value.AcmeChallenge));
        cmd.Parameters.Add("@tokenNonce", SqliteType.Blob).Value = Db(token?.Nonce);
        cmd.Parameters.Add("@tokenCipher", SqliteType.Blob).Value = Db(token?.Ciphertext);
        cmd.Parameters.Add("@tokenTag", SqliteType.Blob).Value = Db(token?.Tag);
    }
}
