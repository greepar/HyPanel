using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyPanel.Server.Live;
using HyPanel.Server.Persistence;
using HyPanel.Shared.Contracts;

namespace HyPanel.Server.Acme;

/// <summary>Where an ACME certificate stands; stored in <c>certificates.acme_status</c>.</summary>
internal static class AcmeStatus
{
    public const string Pending = "Pending";
    public const string Issuing = "Issuing";
    public const string Issued = "Issued";
    public const string Failed = "Failed";

    /// <summary>http-01 / tls-alpn-01 need a node to answer, and no enabled service uses the certificate yet.</summary>
    public const string WaitingForService = "WaitingForService";
}

/// <summary>
/// Obtains an ACME certificate on the Panel and stores it like an uploaded one, so every engine on every node gets
/// the same certificate. <c>dns-01</c> goes through Cloudflare; <c>http-01</c> and <c>tls-alpn-01</c> are proved by the
/// Agents of the nodes whose services use the certificate (the domain points at one of them).
/// </summary>
internal sealed class AcmeIssuer(SqliteServerRepository repository, AcmeChallengeBoard board, ChangeFeed changeFeed,
    IHttpClientFactory clients, IConfiguration configuration, TimeProvider time, ILogger<AcmeIssuer> logger)
{
    public const string LetsEncrypt = "https://acme-v02.api.letsencrypt.org/directory";
    private static readonly TimeSpan NodeReadyTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Overridden in tests (a local ACME server and DNS); production uses Let's Encrypt and Cloudflare.</summary>
    internal Func<string, IAcmeDnsPublisher>? DnsPublisherFactory { get; init; }

    internal HttpClient? AcmeHttpClient { get; init; }

    private Uri DirectoryUrl => new(configuration["HyPanel:AcmeDirectoryUrl"] is { Length: > 0 } url ? url : LetsEncrypt);

    /// <summary>Issues or renews one certificate, recording the outcome on it. Returns whether it was issued.</summary>
    public async Task<bool> IssueAsync(AcmeCertificateWork work, CancellationToken ct)
    {
        var nodes = work.Challenge == "cloudflare" ? [] : await repository.GetCertificateNodesAsync(work.Id, ct);
        if (work.Challenge != "cloudflare" && nodes.Count == 0)
        {
            await repository.SetAcmeStateAsync(work.Id, AcmeStatus.WaitingForService, null, null, ct);
            return false;
        }
        await repository.SetAcmeStateAsync(work.Id, AcmeStatus.Issuing, null, time.GetUtcNow(), ct);
        try
        {
            await RunAsync(work, nodes, ct);
            logger.LogInformation("Issued ACME certificate {Name} for {Domain}.", work.Name, work.Domain);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await repository.SetAcmeStateAsync(work.Id, AcmeStatus.Pending, null, null, CancellationToken.None);
            throw;
        }
        catch (Exception exception) when (exception is AcmeException or HttpRequestException or CryptographicException
                                              or OperationCanceledException or InvalidOperationException)
        {
            var message = exception switch
            {
                AcmeException acme => acme.Message,
                HttpRequestException http => $"无法连接证书颁发机构：{http.Message}",
                OperationCanceledException => "请求证书颁发机构超时。",
                _ => exception.Message
            };
            logger.LogWarning("ACME issuance of {Name} failed: {Error}", work.Name, message);
            await repository.SetAcmeStateAsync(work.Id, AcmeStatus.Failed, message, time.GetUtcNow(), CancellationToken.None);
            return false;
        }
    }

    private async Task RunAsync(AcmeCertificateWork work, IReadOnlyList<(Guid NodeId, string NodeName)> nodes,
        CancellationToken ct)
    {
        var http = AcmeHttpClient ?? clients.CreateClient("acme");
        // One account per certificate, created on its first issuance and reused for renewals.
        using var accountKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (work.AccountKeyPem is not null) accountKey.ImportFromPem(work.AccountKeyPem);
        var client = new AcmeClient(http, DirectoryUrl, accountKey, work.AccountKeyPem is null ? null : work.AccountUrl);
        var accountUrl = await client.EnsureAccountAsync(work.Email, ct);
        if (accountUrl != work.AccountUrl || work.AccountKeyPem is null)
            await repository.SaveAcmeAccountAsync(work.Id, accountUrl, accountKey.ExportPkcs8PrivateKeyPem(), ct);

        var order = await client.NewOrderAsync(work.Domain, ct);
        foreach (var url in order.Authorizations)
        {
            var authorization = await client.GetAuthorizationAsync(url, ct);
            if (authorization.Status == "valid") continue;
            await ProveAsync(client, work, authorization, nodes, ct);
        }

        using var certificateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var chain = await client.FinalizeAsync(order, AcmeClient.CertificateSigningRequest(work.Domain, certificateKey),
            ValidationTimeout, ct);
        var keyPem = certificateKey.ExportPkcs8PrivateKeyPem();
        using var issued = X509Certificate2.CreateFromPem(chain, keyPem);
        await repository.SaveAcmeIssuedAsync(work.Id, chain.Trim() + "\n", keyPem + "\n",
            issued.NotBefore.ToUniversalTime(), issued.NotAfter.ToUniversalTime(),
            Convert.ToHexString(SHA256.HashData(issued.RawData)).ToLowerInvariant(), issued.Subject, time.GetUtcNow(), ct);
        changeFeed.Bump();
    }

    private async Task ProveAsync(AcmeClient client, AcmeCertificateWork work, AcmeAuthorization authorization,
        IReadOnlyList<(Guid NodeId, string NodeName)> nodes, CancellationToken ct)
    {
        var type = work.Challenge switch { "cloudflare" => "dns-01", "tls" => "tls-alpn-01", _ => "http-01" };
        var challenge = authorization.Challenges.FirstOrDefault(item => item.Type == type)
                        ?? throw new AcmeException(authorization.Wildcard
                            ? "泛域名证书只能通过 Cloudflare DNS 验证申请。"
                            : $"证书颁发机构没有提供 {type} 验证。");
        if (type == "dns-01")
        {
            var name = "_acme-challenge." + authorization.Domain.TrimStart('*', '.');
            var dns = DnsPublisherFactory?.Invoke(work.DnsToken ?? "")
                      ?? new CloudflareDns(AcmeHttpClient ?? clients.CreateClient("acme"),
                          work.DnsToken ?? throw new AcmeException("缺少 Cloudflare API Token。"));
            var value = client.DnsValue(challenge.Token);
            var handle = await dns.PublishAsync(name, value, ct);
            try
            {
                await dns.WaitVisibleAsync(name, value, ct);
                await client.RespondAsync(challenge, ct);
                await client.WaitForAuthorizationAsync(authorization.Url, ValidationTimeout, ct);
            }
            finally
            {
                try { await dns.RemoveAsync(handle, CancellationToken.None); }
                catch (Exception exception) when (exception is AcmeException or HttpRequestException)
                {
                    logger.LogWarning("Could not remove the ACME TXT record {Name}: {Error}", name, exception.Message);
                }
            }
            return;
        }

        var assignment = new AcmeChallengeAssignment(type, authorization.Domain, challenge.Token,
            client.KeyAuthorization(challenge.Token));
        board.Post(assignment, nodes.Select(node => node.NodeId).ToHashSet());
        try
        {
            await repository.WakeNodesAsync(nodes.Select(node => node.NodeId), ct);
            changeFeed.Bump();
            await WaitForNodesAsync(challenge.Token, nodes, type, ct);
            await client.RespondAsync(challenge, ct);
            try
            {
                await client.WaitForAuthorizationAsync(authorization.Url, ValidationTimeout, ct);
            }
            catch (AcmeException exception)
            {
                // Name the nodes that could not answer: the CA only says it could not reach the domain.
                var problems = string.Join("；", board.Reports(challenge.Token)
                    .Where(pair => !pair.Value.Ready && pair.Value.Error is not null)
                    .Select(pair => $"{nodes.First(node => node.NodeId == pair.Key).NodeName}：{pair.Value.Error}"));
                throw new AcmeException(problems.Length == 0 ? exception.Message : $"{exception.Message} {problems}");
            }
        }
        finally
        {
            board.Withdraw(challenge.Token);
        }
    }

    /// <summary>
    /// Waits until at least one node answers the challenge (the domain points at one of them) and every node has
    /// reported, or the timeout passes with at least one ready. Fails when none can answer.
    /// </summary>
    private async Task WaitForNodesAsync(string token, IReadOnlyList<(Guid NodeId, string NodeName)> nodes, string type,
        CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + NodeReadyTimeout;
        while (true)
        {
            var reports = board.Reports(token);
            var ready = reports.Values.Count(report => report.Ready);
            if (ready > 0 && reports.Count == nodes.Count) return;
            if (reports.Count == nodes.Count && ready == 0)
                throw new AcmeException(string.Join("；", nodes.Select(node =>
                    $"{node.NodeName}：{reports.GetValueOrDefault(node.NodeId)?.Error ?? "无法开始验证"}")));
            if (time.GetUtcNow() > deadline)
            {
                if (ready > 0) return;
                throw new AcmeException(
                    $"节点没有响应验证请求（{string.Join("、", nodes.Where(node => !reports.ContainsKey(node.NodeId)).Select(node => node.NodeName))}）。" +
                    $"{(type == "http-01" ? "HTTP" : "TLS")} 验证需要节点在线且 Agent 为 0.5.37 或更新版本。");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
        }
    }
}

/// <summary>
/// Issues ACME certificates that have none yet and renews them 30 days before they expire. Runs every ten minutes,
/// and right away when a certificate is saved or the admin asks. A failure is retried after an hour.
/// </summary>
internal sealed class AcmeWorker(SqliteServerRepository repository, AcmeIssuer issuer, TimeProvider time,
    ILogger<AcmeWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    private readonly System.Threading.Channels.Channel<Guid?> requests =
        System.Threading.Channels.Channel.CreateBounded<Guid?>(new System.Threading.Channels.BoundedChannelOptions(64)
        {
            FullMode = System.Threading.Channels.BoundedChannelFullMode.DropOldest
        });

    /// <summary>Runs a pass now; with an id, that certificate is tried even if it failed a moment ago.</summary>
    public void Request(Guid? certificateId = null) => requests.Writer.TryWrite(certificateId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await repository.ResetInterruptedAcmeAsync(stoppingToken);
        Guid? forced = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PassAsync(forced, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning(exception, "ACME pass failed."); }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            wait.CancelAfter(Interval);
            try { forced = await requests.Reader.ReadAsync(wait.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { forced = null; }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task PassAsync(Guid? forced, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var work in await repository.GetAcmeCertificatesAsync(ct))
        {
            if (string.IsNullOrEmpty(work.Domain) || string.IsNullOrEmpty(work.Email)) continue;
            var due = !work.Issued || work.ExpiresAtUtc is null || work.ExpiresAtUtc - now < RenewBefore;
            var resting = work.Status == AcmeStatus.Failed && work.AttemptedAtUtc is { } attempted && now - attempted < RetryAfter;
            if (work.Id == forced || due && !resting) await issuer.IssueAsync(work, ct);
        }
    }
}
