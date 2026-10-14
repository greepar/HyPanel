namespace HyPanel.Shared.Networking;

/// <summary>
/// An external proxy a service sends its outgoing connections through (for example a rented residential proxy), so
/// websites see the proxy's IP instead of the node's. All five config values are optional as a group: an unset type
/// means no proxy, and a half-filled proxy is an error instead of silently connecting directly.
/// </summary>
public sealed record UpstreamProxy(string Type, string Host, int Port, string? Username, string? Password)
{
    public const string Http = "http";
    /// <summary>An HTTP proxy whose connection from this node is wrapped in TLS.</summary>
    public const string Https = "https";
    public const string Socks5 = "socks5";
    private const int MaximumCredentialLength = 255;

    public static IReadOnlyList<string> Types { get; } = [Http, Https, Socks5];

    /// <summary>True for the HTTP family, which carries TCP only; SOCKS5 can also relay UDP.</summary>
    public bool IsHttp => Type is Http or Https;

    /// <summary>
    /// Reads the proxy out of the flat config values. Returns true with a null <paramref name="proxy"/> when no proxy is
    /// configured, and false with <paramref name="error"/> when the values are incomplete or invalid.
    /// </summary>
    public static bool TryCreate(string? type, string? host, int? port, string? username, string? password,
        out UpstreamProxy? proxy, out string? error)
    {
        proxy = null;
        error = null;
        if (string.IsNullOrEmpty(type) && string.IsNullOrEmpty(host) && port is null
            && string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
            return true;
        if (string.IsNullOrEmpty(type) || !Types.Contains(type, StringComparer.Ordinal))
            return Fail("上游代理类型必须是 http、https 或 socks5。", out error);
        if (!IsValidHost(host)) return Fail("上游代理地址无效，请填写域名或 IP。", out error);
        if (port is not (>= 1 and <= 65535)) return Fail("上游代理端口必须在 1 到 65535 之间。", out error);
        if (!IsValidCredential(username) || !IsValidCredential(password))
            return Fail("上游代理的用户名和密码不能包含控制字符，且不能超过 255 个字符。", out error);
        if (!string.IsNullOrEmpty(password) && string.IsNullOrEmpty(username))
            return Fail("填写了上游代理密码时，还需要填写用户名。", out error);
        proxy = new UpstreamProxy(type, host!, port!.Value, NullIfEmpty(username), NullIfEmpty(password));
        return true;
    }

    /// <summary>The proxy as a URL for tools that take one; credentials are percent-encoded.</summary>
    public string ToUrl()
    {
        var authority = Host.Contains(':') ? $"[{Host}]" : Host;
        var credentials = Username is null ? "" : Password is null
            ? Uri.EscapeDataString(Username) + "@"
            : Uri.EscapeDataString(Username) + ":" + Uri.EscapeDataString(Password) + "@";
        return $"{(Type == Https ? "https" : Type == Http ? "http" : "socks5")}://{credentials}{authority}:{Port}";
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool IsValidHost(string? host) =>
        !string.IsNullOrEmpty(host) && host.Length <= 253 && host.All(static c => c > ' ' && c != 127)
        && !host.Contains('/') && !host.Contains('@') && !host.Contains('[') && !host.Contains(']')
        && Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6;

    private static bool IsValidCredential(string? value) =>
        value is null || value.Length <= MaximumCredentialLength && value.All(static c => !char.IsControl(c));
}
