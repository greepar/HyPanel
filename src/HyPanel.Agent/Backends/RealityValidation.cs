using System.Net;
using System.Net.Sockets;

namespace HyPanel.Agent.Backends;

/// <summary>REALITY settings checks shared by every engine that serves VLESS + REALITY (Xray, Mihomo).</summary>
internal static class RealityValidation
{
    public const string VisionFlow = "xtls-rprx-vision";

    /// <summary>
    /// Client TLS fingerprints the editor offers (the Panel catalog's list). Only clients use it; the server checks it
    /// so a service never ships a value its subscription clients would reject.
    /// </summary>
    public static bool IsFingerprint(string? value) =>
        value is "chrome" or "firefox" or "safari" or "ios" or "android" or "edge" or "random";

    public static bool IsKey(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('=')
            || !value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            return Convert.FromBase64String(padded).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsShortId(string? value) =>
        value is { Length: >= 2 and <= 16 } && value.Length % 2 == 0 && value.All(char.IsAsciiHexDigitLower);

    /// <summary><c>host:port</c> or <c>[ipv6]:port</c> of the site REALITY borrows its handshake from.</summary>
    public static bool IsDestination(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string portText;
        if (value[0] == '[')
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket < 2 || closingBracket + 1 >= value.Length || value[closingBracket + 1] != ':')
                return false;
            var host = value[1..closingBracket];
            portText = value[(closingBracket + 2)..];
            if (!IsCanonicalIpAddress(host) || IPAddress.Parse(host).AddressFamily != AddressFamily.InterNetworkV6)
                return false;
        }
        else
        {
            var separator = value.LastIndexOf(':');
            if (separator < 1 || separator != value.IndexOf(':')) return false;
            var host = value[..separator];
            portText = value[(separator + 1)..];
            if (!(IsCanonicalIpAddress(host) || IsDnsHostname(host))) return false;
        }

        return int.TryParse(portText, out var port) && port is >= 1 and <= 65_535 && portText == port.ToString();
    }

    public static bool IsCanonicalIpAddress(string? value) =>
        !string.IsNullOrEmpty(value) && IPAddress.TryParse(value, out var address)
        && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && string.Equals(value, address.ToString(), StringComparison.Ordinal);

    public static bool IsDnsHostname(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253 || value.EndsWith('.')) return false;
        return value.Split('.').All(label => label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-'
                                             && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }
}
