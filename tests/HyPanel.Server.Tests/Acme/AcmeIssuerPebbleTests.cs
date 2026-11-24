extern alias agent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using agent::HyPanel.Agent.Networking;
using HyPanel.Server.Acme;
using HyPanel.Server.Endpoints;
using HyPanel.Server.Live;
using HyPanel.Server.Persistence;
using HyPanel.Server.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Acme;

/// <summary>
/// End to end against Pebble, Let's Encrypt's test CA, with its mock DNS server: every validation method, the real
/// issuer, repository and the Agent's challenge responder. Runs when HYPANEL_PEBBLE_DIR points at a directory with
/// the <c>pebble</c> and <c>pebble-challtestsrv</c> binaries and Pebble's <c>test/</c> folder; skipped otherwise.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AcmeIssuerPebbleTests
{
    private static readonly string? PebbleDirectory = Environment.GetEnvironmentVariable("HYPANEL_PEBBLE_DIR");
    private static Process? pebble;
    private static Process? dns;

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        if (string.IsNullOrEmpty(PebbleDirectory)) return;
        dns = Start("pebble-challtestsrv", "-defaultIPv4", "127.0.0.1", "-defaultIPv6", "", "-http01", "", "-https01", "",
            "-tlsalpn01", "", "-doh", "", "-dnsserver", ":8053", "-management", ":8055");
        pebble = Start("pebble", "-config", "test/config/pebble-config.json", "-dnsserver", "127.0.0.1:8053");
        using var client = Client();
        Exception? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try { (await client.GetAsync("https://127.0.0.1:14000/dir")).EnsureSuccessStatusCode(); return; }
            catch (HttpRequestException exception) { last = exception; await Task.Delay(200); }
        }
        throw new InvalidOperationException($"Pebble did not start: {last?.Message} {last?.InnerException?.Message}");
    }

    [ClassCleanup]
    public static void Stop()
    {
        foreach (var process in new[] { pebble, dns })
        {
            if (process is null) continue;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            process.Dispose();
        }
    }

    [TestMethod]
    public Task Dns01_IssuesAWildcardThroughTheDnsPublisher() => IssueAsync("*.example.com", "cloudflare");

    [TestMethod]
    public Task Http01_IsProvedByTheAgentOfTheNodeUsingTheCertificate() => IssueAsync("hy.example.com", "http");

    [TestMethod]
    public Task TlsAlpn01_IsProvedByTheAgentOfTheNodeUsingTheCertificate() => IssueAsync("tls.example.com", "tls");

    private static async Task IssueAsync(string domain, string challenge)
    {
        if (string.IsNullOrEmpty(PebbleDirectory)) Assert.Inconclusive("Set HYPANEL_PEBBLE_DIR to run the ACME end-to-end tests.");
        await using var fixture = await Fixture.CreateAsync();
        var repository = fixture.Repository;
        Assert.IsTrue(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(), new CertificateUploadRequest("acme", null, null,
            "Acme", Domain: domain, AcmeEmail: "ops@example.com", AcmeChallenge: challenge,
            AcmeDnsToken: challenge == "cloudflare" ? "token" : null), DateTimeOffset.UtcNow, out var certificate));
        Assert.IsTrue(await repository.CreateCertificateAsync(certificate, CancellationToken.None));

        // A node with a service using the certificate: its "Agent" proves http-01 / tls-alpn-01 challenges.
        var nodeId = Guid.NewGuid();
        await repository.CreateNodeAsync(nodeId, "edge", CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await repository.CreateServiceAsync(new ServiceInstanceRecord(Guid.NewGuid(), nodeId, "hy2", "hysteria2", "2.14.0",
            true, 1, $$"""{"listenHost":"0.0.0.0","listenPort":8443,"certificateId":"{{certificate.Id}}"}""", now, now),
            CancellationToken.None);

        var board = new AcmeChallengeBoard();
        using var http = Client();
        var issuer = new AcmeIssuer(repository, board, new ChangeFeed(), null!, new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["HyPanel:AcmeDirectoryUrl"] = "https://127.0.0.1:14000/dir" })
                .Build(), TimeProvider.System, NullLogger<AcmeIssuer>.Instance)
        {
            AcmeHttpClient = http,
            DnsPublisherFactory = _ => new ChallTestSrvDns(http)
        };

        using var responder = new AcmeChallengeResponder(httpPort: 5002, tlsPort: 5001);
        using var stopAgent = new CancellationTokenSource();
        var agentLoop = Task.Run(async () =>
        {
            while (!stopAgent.IsCancellationRequested)
            {
                responder.Apply(board.For(nodeId));
                board.Record(nodeId, responder.Reports());
                await Task.Delay(300);
            }
        });

        var work = (await repository.GetAcmeCertificatesAsync(CancellationToken.None)).Single();
        var issued = await issuer.IssueAsync(work, CancellationToken.None);

        var stored = await repository.GetCertificateWithKeyAsync(certificate.Id, CancellationToken.None);
        Assert.IsTrue(issued, stored?.AcmeError);
        Assert.AreEqual(AcmeStatus.Issued, stored!.AcmeStatus);
        using var pair = X509Certificate2.CreateFromPem(stored.CertificatePem, stored.PrivateKeyPem);
        CollectionAssert.Contains(pair.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single()
            .EnumerateDnsNames().ToArray(), domain);
        Assert.IsNotNull(stored.Fingerprint);
        Assert.AreEqual(0, board.For(nodeId).Count, "the challenge is withdrawn afterwards");

        // A renewal reuses the account it registered the first time.
        var renewal = (await repository.GetAcmeCertificatesAsync(CancellationToken.None)).Single();
        Assert.IsNotNull(renewal.AccountUrl);
        Assert.IsTrue(await issuer.IssueAsync(renewal, CancellationToken.None));
        Assert.AreEqual(renewal.AccountUrl,
            (await repository.GetAcmeCertificatesAsync(CancellationToken.None)).Single().AccountUrl);
        await stopAgent.CancelAsync();
        await agentLoop;
    }

    private static Process Start(string binary, params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(PebbleDirectory!, binary))
        {
            WorkingDirectory = PebbleDirectory, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PEBBLE_VA_NOSLEEP"] = "1";
        var process = Process.Start(start)!;
        var log = Path.Combine(PebbleDirectory!, binary + ".test.log");
        File.WriteAllText(log, "");
        process.OutputDataReceived += (_, line) => { if (line.Data is not null) lock (log) File.AppendAllText(log, line.Data + "\n"); };
        process.ErrorDataReceived += (_, line) => { if (line.Data is not null) lock (log) File.AppendAllText(log, line.Data + "\n"); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    /// <summary>Pebble serves its API with a test certificate; only this test trusts it.</summary>
    private static HttpClient Client()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HyPanel-Tests/1.0");
        return client;
    }

    /// <summary>TXT records in Pebble's mock DNS server, standing in for Cloudflare.</summary>
    private sealed class ChallTestSrvDns(HttpClient http) : IAcmeDnsPublisher
    {
        public async Task<string> PublishAsync(string name, string value, CancellationToken ct)
        {
            (await http.PostAsync("http://127.0.0.1:8055/set-txt",
                new StringContent($$"""{"host":"{{name}}.","value":"{{value}}"}"""), ct)).EnsureSuccessStatusCode();
            return name;
        }

        public async Task RemoveAsync(string handle, CancellationToken ct) =>
            (await http.PostAsync("http://127.0.0.1:8055/clear-txt",
                new StringContent($$"""{"host":"{{handle}}."}"""), ct)).EnsureSuccessStatusCode();

        public Task WaitVisibleAsync(string name, string value, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory;

        private Fixture(string directory, SqliteServerRepository repository)
        {
            this.directory = directory;
            Repository = repository;
        }

        public SqliteServerRepository Repository { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HyPanel.Server.Tests", "acme-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:HyPanel"] = $"Data Source={Path.Combine(directory, "server.db")}",
                ["HyPanel:Security:MasterKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
            }).Build();
            var factory = new SqliteConnectionFactory(configuration);
            await new SqliteMigrationRunner(factory, TimeProvider.System).MigrateAsync(CancellationToken.None);
            return new Fixture(directory, new SqliteServerRepository(factory, TimeProvider.System,
                new ProxyCredentialProtector(configuration)));
        }

        public ValueTask DisposeAsync()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
            return ValueTask.CompletedTask;
        }
    }
}
