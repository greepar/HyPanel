namespace HyPanel.Agent;

using HyPanel.Agent.Backends;
using HyPanel.Agent.Backends.Hysteria2;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Backends.Mihomo;
using HyPanel.Agent.Backends.SingBox;
using HyPanel.Agent.Backends.Xray;
using HyPanel.Agent.Reconciliation;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        var enrollmentOptions = AgentEnrollmentOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(enrollmentOptions);
        builder.Services.AddSingleton<AgentCredentialStore>();
        builder.Services.AddSingleton<IEnrollmentClient, EnrollmentClient>();
        builder.Services.AddSingleton<AgentIdentityManager>();
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<HttpMessageHandler>(_ => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton(sp =>
            new HttpClient(sp.GetRequiredService<HttpMessageHandler>(), disposeHandler: false)
            {
                Timeout = Timeout.InfiniteTimeSpan
            });
        builder.Services.AddSingleton<AgentStateStore>();
        builder.Services.AddSingleton<AgentCommandStateStore>();
        builder.Services.AddSingleton<AgentUsageStateStore>();
        builder.Services.AddSingleton<NodeMetricsCollector>();
        builder.Services.AddSingleton<IBackendProvider, Hysteria2Provider>();
        builder.Services.AddSingleton<IBackendProvider, XrayProvider>();
        builder.Services.AddSingleton<IBackendProvider, MihomoProvider>();
        builder.Services.AddSingleton<IBackendProvider, SingBoxProvider>();
        builder.Services.AddSingleton<BackendProviderRegistry>();
        builder.Services.AddSingleton<BackendBinaryManager>();
        builder.Services.AddSingleton<BackendInstanceStore>();
        builder.Services.AddSingleton<BackendProcessSupervisor>();
        builder.Services.AddSingleton<ServiceReconciler>();
        builder.Services.AddHostedService<SyncWorker>();

        await builder.Build().RunAsync();
    }
}