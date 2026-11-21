using System.Diagnostics.CodeAnalysis;

namespace HyPanel.Server.Backends;

/// <summary>Shortcuts for the feature checks the persistence layer and endpoints make by backend type.</summary>
internal static class BackendCapabilities
{
    /// <summary>
    /// Backends whose Agent provider renders one distinct identity per granted user and reports per-user traffic.
    /// Only these receive user grants; their subscription entries use the per-binding credential.
    /// </summary>
    public static bool IsMultiUser([NotNullWhen(true)] string? backendType) =>
        BackendDefinitionCatalog.Has(backendType, BackendFeatures.MultiUser);

    /// <summary>Backends that need a loopback control port for their local stats API.</summary>
    public static bool NeedsControlPort([NotNullWhen(true)] string? backendType) =>
        BackendDefinitionCatalog.Has(backendType, BackendFeatures.ControlPort);

    public static string MultiUserSqlList { get; } = BackendDefinitionCatalog.SqlList(BackendFeatures.MultiUser);
}
