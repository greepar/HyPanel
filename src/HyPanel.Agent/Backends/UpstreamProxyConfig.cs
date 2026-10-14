namespace HyPanel.Agent.Backends;

using HyPanel.Shared.Networking;

/// <summary>The flat config values every backend uses to describe an optional upstream proxy.</summary>
internal interface IUpstreamProxyConfig
{
    string? UpstreamProxyType { get; }
    string? UpstreamProxyHost { get; }
    int? UpstreamProxyPort { get; }
    string? UpstreamProxyUsername { get; }
    string? UpstreamProxyPassword { get; }
}

internal static class UpstreamProxyConfig
{
    /// <summary>True when the proxy is absent (<paramref name="proxy"/> null) or complete and valid.</summary>
    public static bool TryGetProxy(this IUpstreamProxyConfig config, out UpstreamProxy? proxy) =>
        UpstreamProxy.TryCreate(config.UpstreamProxyType, config.UpstreamProxyHost, config.UpstreamProxyPort,
            config.UpstreamProxyUsername, config.UpstreamProxyPassword, out proxy, out _);

    /// <summary>The configured proxy, or null for a direct connection. Call only on a config that already validated.</summary>
    public static UpstreamProxy? ProxyOrNull(this IUpstreamProxyConfig config) =>
        config.TryGetProxy(out var proxy) ? proxy : null;
}
