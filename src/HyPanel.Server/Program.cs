namespace HyPanel.Server;

using System.Text.Json.Serialization;
using HyPanel.Server.Endpoints;
using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;
using HyPanel.Shared.Serialization;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HyPanelJsonSerializerContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, ServerJsonSerializerContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HealthJsonSerializerContext.Default);
        });
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<AdminTokenAuthentication>();
        builder.Services.AddSingleton<AdminAuthorization>();
        builder.Services.AddSingleton<AgentAuthentication>();
        builder.Services.AddSingleton<UserAuthentication>();
        builder.Services.AddSingleton<PasswordService>();
        builder.Services.AddSingleton<SqliteConnectionFactory>();
        builder.Services.AddSingleton<SqliteMigrationRunner>();
        builder.Services.AddSingleton<SqliteServerRepository>();
        builder.Services.AddSingleton<EnrollmentService>();
        builder.Services.AddSingleton<ReleaseCatalog>();
        builder.Services.AddSingleton<BackendArtifactCatalog>();

        var app = builder.Build();

        _ = app.Services.GetRequiredService<AdminTokenAuthentication>();
        _ = app.Services.GetRequiredService<ReleaseCatalog>();
        _ = app.Services.GetRequiredService<BackendArtifactCatalog>();
        await app.Services.GetRequiredService<SqliteMigrationRunner>().MigrateAsync(CancellationToken.None);

        app.MapGet("/health", static () =>
            Results.Json(HealthResponse.Instance, HealthJsonSerializerContext.Default.HealthResponse));
        AdminNodesEndpoints.Map(app);
        AdminServicesEndpoints.Map(app);
        AdminServiceTemplateEndpoints.Map(app);
        AdminObservationEndpoints.Map(app);
        AdminHealthSummaryEndpoints.Map(app);
        AdminEnrollmentTokensEndpoints.Map(app);
        AgentEnrollmentEndpoints.Map(app);
        AgentSyncEndpoints.Map(app);
        UserEndpoints.Map(app);
        SubscriptionEndpoints.Map(app);
        ReleaseEndpoints.Map(app);
        EmbeddedWebEndpoints.Map(app);

        await app.RunAsync();
    }
}

internal sealed record HealthResponse(string Status)
{
    public static readonly HealthResponse Instance = new("ok");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(HealthResponse))]
internal sealed partial class HealthJsonSerializerContext : JsonSerializerContext;
