namespace HyPanel.Agent;

public sealed record AgentEnrollmentOptions(
    string? PanelUrl,
    string? EnrollmentToken,
    string DataDirectory)
{
    public static AgentEnrollmentOptions FromConfiguration(IConfiguration configuration)
    {
        var dataDirectory = configuration["HYPANEL_DATA_DIR"];
        if (string.IsNullOrWhiteSpace(dataDirectory))
        {
            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                localApplicationData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".local",
                    "share");
            }

            dataDirectory = Path.Combine(localApplicationData, "HyPanel");
        }

        return new AgentEnrollmentOptions(
            configuration["HYPANEL_PANEL_URL"],
            configuration["HYPANEL_ENROLLMENT_TOKEN"],
            dataDirectory);
    }
}
