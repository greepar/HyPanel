namespace HyPanel.Server.Backends;

using System.Diagnostics.CodeAnalysis;

/// <summary>
/// Backends whose Agent provider renders one distinct identity per granted user and reports per-user traffic.
/// Only these receive user grants; their subscription entries use the per-binding credential.
/// </summary>
internal static class BackendCapabilities
{
    public static bool IsMultiUser([NotNullWhen(true)] string? backendType) => backendType is "xray" or "hysteria2";

    /// <summary>Backends that need a loopback control port for their local stats API.</summary>
    public static bool NeedsControlPort([NotNullWhen(true)] string? backendType) => IsMultiUser(backendType);

    public const string MultiUserSqlList = "('xray','hysteria2')";
}
