namespace HyPanel.Server;

internal static class ServerDataDirectory
{
    public static string Resolve(IConfiguration configuration)
    {
        var configured = configuration["HYPANEL_DATA_DIR"] ?? configuration["HyPanel:DataDirectory"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? Environment.CurrentDirectory : configured,
            Environment.CurrentDirectory);
    }
}
