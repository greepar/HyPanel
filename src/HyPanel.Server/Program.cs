namespace HyPanel.Server;

using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HyPanel.Server.Endpoints;
using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;
using HyPanel.Server.Security;
using HyPanel.Server.Updates;
using HyPanel.Server.Backup;
using HyPanel.Shared.Serialization;
using Microsoft.AspNetCore.HttpOverrides;

public static class Program
{
    public static async Task Main(string[] args)
    {
        if (args is ["--self-test"])
        {
            Console.Out.WriteLine($"{ServerBuildInfo.Version}\t{ServerBuildInfo.RuntimeIdentifier}");
            return;
        }
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HyPanelJsonSerializerContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonSerializerContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HealthJsonSerializerContext.Default);
        });
        builder.Services.Configure<ForwardedHeadersOptions>(ConfigureForwardedHeaders);
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<AdminTokenAuthentication>();
        builder.Services.AddSingleton<AdminAuthorization>();
        builder.Services.AddSingleton<AgentAuthentication>();
        builder.Services.AddSingleton<UserAuthentication>();
        builder.Services.AddSingleton<PasswordService>();
        builder.Services.AddSingleton<ProxyCredentialProtector>();
        builder.Services.AddSingleton<ServerProcessControl>();
        builder.Services.AddSingleton<ServerOperationCoordinator>();
        builder.Services.AddSingleton<BackupRestoreService>();
        builder.Services.AddHttpClient("server-update", client => client.Timeout = TimeSpan.FromMinutes(10));
        builder.Services.AddSingleton<ServerUpdateService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerUpdateService>());
        builder.Services.AddSingleton<SqliteConnectionFactory>();
        builder.Services.AddSingleton<SqliteMigrationRunner>();
        builder.Services.AddSingleton<SqliteServerRepository>();
        builder.Services.AddSingleton<EnrollmentService>();
        builder.Services.AddSingleton<ReleaseCatalog>();
        builder.Services.AddHttpClient("release-sync", client => client.Timeout = TimeSpan.FromMinutes(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = true });
        builder.Services.AddHostedService<ReleaseSyncWorker>();
        builder.Services.AddSingleton<BackendArtifactCatalog>();
        builder.Services.AddHttpClient("backend-release-sync", client => client.Timeout = TimeSpan.FromMinutes(10));
        builder.Services.AddSingleton<BackendReleaseSyncWorker>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackendReleaseSyncWorker>());
        builder.Services.AddSingleton<InstallCodeService>();
        builder.Services.AddRateLimiter(options => options.AddPolicy("install-code", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                static _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                })));

        var app = builder.Build();
        Directory.CreateDirectory(ServerDataDirectory.Resolve(app.Configuration));
        if (args.Length > 0 && args[0] is "backup" or "restore")
        {
            Environment.ExitCode = await RunBackupCliAsync(args, app.Services, app.Configuration);
            return;
        }
        using var processLock = ServerProcessLock.Acquire(ServerDataDirectory.Resolve(app.Configuration));
        await app.Services.GetRequiredService<BackupRestoreService>().ApplyPendingRestoreAsync(CancellationToken.None);

        app.UseForwardedHeaders();
        app.UseRateLimiter();

        _ = app.Services.GetRequiredService<AdminTokenAuthentication>();
        _ = app.Services.GetRequiredService<ReleaseCatalog>();
        _ = app.Services.GetRequiredService<BackendArtifactCatalog>();
        _ = app.Services.GetRequiredService<ProxyCredentialProtector>();
        await app.Services.GetRequiredService<SqliteMigrationRunner>().MigrateAsync(CancellationToken.None);
        await app.Services.GetRequiredService<SqliteServerRepository>()
            .InitializeProxyCredentialsAsync(CancellationToken.None);
        await app.Services.GetRequiredService<SqliteServerRepository>()
            .InitializeCertificatesAsync(CancellationToken.None);
        await app.Services.GetRequiredService<SqliteServerRepository>()
            .SyncAllUserBindingsAsync(CancellationToken.None);

        app.MapGet("/health", static () =>
            Results.Json(HealthResponse.Instance, HealthJsonSerializerContext.Default.HealthResponse));
        AdminNodesEndpoints.Map(app);
        AdminServicesEndpoints.Map(app);
        AdminCertificatesEndpoints.Map(app);
        AdminBackendEndpoints.Map(app);
        AdminServiceBatchEndpoints.Map(app);
        AdminGroupEndpoints.Map(app);
        SubscriptionTemplateEndpoints.Map(app);
        AdminObservationEndpoints.Map(app);
        AdminHealthSummaryEndpoints.Map(app);
        AdminDiagnosticsEndpoints.Map(app);
        AdminEnrollmentTokensEndpoints.Map(app);
        AgentEnrollmentEndpoints.Map(app);
        AgentSyncEndpoints.Map(app);
        UserEndpoints.Map(app);
        SubscriptionEndpoints.Map(app);
        ReleaseEndpoints.Map(app);
        ServerUpdateEndpoints.Map(app);
        GlobalSettingsEndpoints.Map(app);
        AdminBackupEndpoints.Map(app);
        EmbeddedWebEndpoints.Map(app);

        await app.RunAsync();
    }

    private static async Task<int> RunBackupCliAsync(string[] args, IServiceProvider services,
        IConfiguration configuration)
    {
        var backups = services.GetRequiredService<BackupRestoreService>();
        try
        {
            if (args is ["backup"])
            {
                var value = await backups.CreateAsync(CancellationToken.None);
                Console.WriteLine(Path.Combine(backups.BackupDirectory, value.Id));
                return 0;
            }
            if (args is ["backup", "validate", var validationPath])
            {
                var value = await backups.ValidateFileAsync(validationPath, CancellationToken.None);
                Console.WriteLine(value.Valid ? "valid" : value.Error);
                return value.Valid ? 0 : 2;
            }
            if (args is ["restore", var restorePath])
            {
                using var processLock = ServerProcessLock.Acquire(ServerDataDirectory.Resolve(configuration));
                await backups.RestoreFileOfflineAsync(restorePath, CancellationToken.None);
                Console.WriteLine("restore_succeeded");
                return 0;
            }
            Console.Error.WriteLine("Usage: HyPanel.Server backup | backup validate <archive> | restore <archive>");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception is BackupException backup ? backup.Code : exception.Message);
            return 1;
        }
    }

    internal static void ConfigureForwardedHeaders(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                   ForwardedHeaders.XForwardedProto |
                                   ForwardedHeaders.XForwardedHost;
        options.ForwardLimit = 1;
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
    }
}

internal sealed record HealthResponse(string Status)
{
    public static readonly HealthResponse Instance = new("ok");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class HealthJsonSerializerContext : JsonSerializerContext;
