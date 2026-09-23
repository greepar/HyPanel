namespace HyPanel.Server.Security;

using HyPanel.Server.Endpoints;
using HyPanel.Server.Persistence;

/// <summary>
/// Re-reads Path certificates from the Panel host every minute. When the files hold a different valid certificate
/// (for example after an external tool renewed it), the stored copy is replaced and every node using it receives the
/// new certificate on its next sync. Invalid or unreadable files never replace the current certificate.
/// </summary>
internal sealed class CertificateSourceWatcher(
    SqliteServerRepository repository,
    TimeProvider time,
    ILogger<CertificateSourceWatcher> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllAsync(stoppingToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Checking path-mapped certificates failed.");
            }
            await Task.Delay(Interval, time, stoppingToken);
        }
    }

    internal async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var certificate in (await repository.GetCertificatesAsync(ct)).Where(item => item.Kind == "Path"))
            await CheckAsync(certificate, ct);
    }

    private async Task CheckAsync(CertificateRecord certificate, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        if (certificate.CertificatePath is not { } certificatePath || certificate.PrivateKeyPath is not { } keyPath)
            return;
        if (!CertificateFileSource.TryRead(certificatePath, keyPath, out var pem, out var key, out var error))
        {
            await repository.SetCertificateSourceStatusAsync(certificate.Id, now, error, ct);
            return;
        }
        var request = new CertificateUploadRequest(certificate.Name, pem, key, "Path", certificatePath, keyPath);
        if (!AdminCertificatesEndpoints.TryCreate(certificate.Id, request, now, out var loaded))
        {
            await repository.SetCertificateSourceStatusAsync(certificate.Id, now,
                "文件中的证书无效、已过期或与私钥不匹配，继续使用上一次的证书。", ct);
            return;
        }
        if (loaded.Fingerprint != certificate.Fingerprint)
        {
            var replacement = loaded with { CreatedAtUtc = certificate.CreatedAtUtc, UsedBy = certificate.UsedBy };
            if (await repository.ReplaceCertificateAsync(replacement, ct))
                logger.LogInformation("Certificate {Name} changed on disk; distributed new certificate valid until {Expires}.",
                    certificate.Name, loaded.ExpiresAtUtc);
        }
        await repository.SetCertificateSourceStatusAsync(certificate.Id, now, null, ct);
    }
}
