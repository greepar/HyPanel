namespace HyPanel.Server;

using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using HyPanel.Server.Endpoints;
using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;
using HyPanel.Server.Security;
using HyPanel.Server.Updates;
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

        app.UseForwardedHeaders();
        app.UseRateLimiter();

        _ = app.Services.GetRequiredService<AdminTokenAuthentication>();
        _ = app.Services.GetRequiredService<ReleaseCatalog>();
        _ = app.Services.GetRequiredService<BackendArtifactCatalog>();
        _ = app.Services.GetRequiredService<ProxyCredentialProtector>();
        await app.Services.GetRequiredService<SqliteMigrationRunner>().MigrateAsync(CancellationToken.None);
        await app.Services.GetRequiredService<SqliteServerRepository>()
            .InitializeProxyCredentialsAsync(CancellationToken.None);

        app.MapGet("/health", static () =>
            Results.Json(HealthResponse.Instance, HealthJsonSerializerContext.Default.HealthResponse));
        AdminNodesEndpoints.Map(app);
        AdminServicesEndpoints.Map(app);
        AdminServiceTemplateEndpoints.Map(app);
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
        EmbeddedWebEndpoints.Map(app);

        await app.RunAsync();
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
