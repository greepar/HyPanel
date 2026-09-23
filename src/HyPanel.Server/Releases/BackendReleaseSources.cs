namespace HyPanel.Server.Releases;

internal sealed record BackendReleaseSource(string BackendType, string Repository,
    Func<string, string, string?> AssetName, Func<string, string> ExecutablePath);

internal static class BackendReleaseSources
{
    public static readonly IReadOnlyList<BackendReleaseSource> All =
    [
        new("hysteria2", "HyNetwork/hysteria", HysteriaAsset, static _ => string.Empty),
        new("xray", "XTLS/Xray-core", XrayAsset, static rid => rid.StartsWith("win-", StringComparison.Ordinal) ? "xray.exe" : "xray")
    ];

    public static bool IsBackendType(string value) => All.Any(item => item.BackendType == value);
    public static BackendReleaseSource Get(string backendType) => All.Single(item => item.BackendType == backendType);

    private static string? HysteriaAsset(string rid, string version) => rid switch
    {
        "linux-x64" or "linux-musl-x64" => "hysteria-linux-amd64",
        "linux-arm64" or "linux-musl-arm64" => "hysteria-linux-arm64",
        "osx-x64" => "hysteria-darwin-amd64",
        "osx-arm64" => "hysteria-darwin-arm64",
        "win-x64" => "hysteria-windows-amd64.exe",
        "win-arm64" => "hysteria-windows-arm64.exe",
        _ => null
    };

    private static string? XrayAsset(string rid, string version) => rid switch
    {
        "linux-x64" or "linux-musl-x64" => "Xray-linux-64.zip",
        "linux-arm64" or "linux-musl-arm64" => "Xray-linux-arm64-v8a.zip",
        "osx-x64" => "Xray-macos-64.zip",
        "osx-arm64" => "Xray-macos-arm64-v8a.zip",
        "win-x64" => "Xray-windows-64.zip",
        "win-arm64" => "Xray-windows-arm64-v8a.zip",
        _ => null
    };


}
