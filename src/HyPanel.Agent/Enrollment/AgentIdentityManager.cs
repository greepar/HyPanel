namespace HyPanel.Agent;

using System.Runtime.InteropServices;
using HyPanel.Shared.Contracts;

public sealed class AgentIdentityManager(
    AgentCredentialStore credentialStore,
    IEnrollmentClient enrollmentClient,
    AgentEnrollmentOptions options,
    IHostEnvironment hostEnvironment,
    ILogger<AgentIdentityManager> logger)
{
    public async Task<AgentCredentials> EnsureIdentityAsync(CancellationToken cancellationToken)
    {
        var existingCredentials = await credentialStore.TryLoadAsync(cancellationToken);
        if (existingCredentials is not null)
        {
            logger.LogInformation("Loaded existing Agent credentials.");
            return existingCredentials;
        }

        var panelBaseUri = ValidatePanelUrl(options.PanelUrl, hostEnvironment.IsDevelopment());
        if (string.IsNullOrWhiteSpace(options.EnrollmentToken))
        {
            throw new InvalidOperationException(
                "HYPANEL_ENROLLMENT_TOKEN is required when Agent credentials do not exist.");
        }

        logger.LogInformation("Enrolling Agent with the configured panel.");
        var enrollmentRequest = new AgentEnrollmentRequest(
            options.EnrollmentToken,
            GetAgentVersion(),
            GetPlatform(),
            new AgentMachineInfo(Environment.MachineName));
        var enrollmentResponse = await enrollmentClient.EnrollAsync(
            panelBaseUri,
            enrollmentRequest,
            cancellationToken);

        var credentials = new AgentCredentials(
            panelBaseUri.AbsoluteUri.TrimEnd('/'),
            enrollmentResponse.AgentId,
            enrollmentResponse.AgentSecret,
            enrollmentResponse.NodeId,
            enrollmentResponse.SyncIntervalSeconds);
        await credentialStore.SaveAsync(credentials, cancellationToken);

        logger.LogInformation("Agent enrollment completed.");
        return credentials;
    }

    private static Uri ValidatePanelUrl(string? panelUrl, bool isDevelopment)
    {
        if (!Uri.TryCreate(panelUrl, UriKind.Absolute, out var panelBaseUri) ||
            string.IsNullOrWhiteSpace(panelBaseUri.Host))
        {
            throw new InvalidOperationException("HYPANEL_PANEL_URL must be an absolute URL.");
        }

        if (panelBaseUri.Scheme == Uri.UriSchemeHttps)
        {
            return panelBaseUri;
        }

        if (isDevelopment &&
            panelBaseUri.Scheme == Uri.UriSchemeHttp &&
            string.Equals(panelBaseUri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return panelBaseUri;
        }

        throw new InvalidOperationException(
            "HYPANEL_PANEL_URL must use HTTPS; HTTP is allowed only for localhost in Development.");
    }

    private static string GetAgentVersion() =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string GetPlatform()
    {
        var operatingSystem = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : RuntimeInformation.OSDescription.Trim().ToLowerInvariant().Replace(' ', '-');
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();

        return $"{operatingSystem}-{architecture}";
    }
}
