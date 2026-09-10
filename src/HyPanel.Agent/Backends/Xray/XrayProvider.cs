namespace HyPanel.Agent.Backends.Xray;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Shared.Contracts;

public sealed class XrayProvider : IBackendProvider
{
    private const string VisionFlow = "xtls-rprx-vision";

    public string BackendType => "xray";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.Users |
        BackendCapabilities.Logs |
        BackendCapabilities.VersionQuery |
        BackendCapabilities.ConfigValidation;

    public ValueTask<BackendValidationResult> ValidateAsync(ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (desiredState.ConfigSchemaVersion != 1)
        {
            return ValueTask.FromResult(Invalid("unsupported_schema", "Unsupported Xray configuration schema."));
        }

        if (!TryParseConfig(desiredState.ConfigJson, out var config) || !IsValidConfig(config))
        {
            return ValueTask.FromResult(Invalid("invalid_config", "Invalid Xray configuration."));
        }

        return ValueTask.FromResult(new BackendValidationResult(true, [config.ListenPort], Array.Empty<int>(), null,
            null));
    }

    public ValueTask<RenderedBackendConfig> RenderConfigAsync(ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (desiredState.ConfigSchemaVersion != 1 || !TryParseConfig(desiredState.ConfigJson, out var config) ||
            !IsValidConfig(config))
        {
            throw new InvalidOperationException("Xray configuration is invalid.");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(CreateRuntimeConfig(config),
            XrayRuntimeJsonSerializerContext.Default.XrayRuntimeConfig);
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return ValueTask.FromResult(new RenderedBackendConfig("config.json", content, sha256));
    }

    public ValueTask<BackendProcessSpec> CreateProcessSpecAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new BackendProcessSpec(instance.BinaryPath, ["run", "-c", instance.ConfigPath],
            instance.InstanceDirectory, new Dictionary<string, string>()));
    }

    public ValueTask<BackendHealthResult> CheckHealthAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(instance.InstanceDirectory) || !File.Exists(instance.ConfigPath))
        {
            return ValueTask.FromResult(new BackendHealthResult(false, "instance_files_missing",
                "Xray instance files are missing."));
        }

        return ValueTask.FromResult(new BackendHealthResult(true, null, null));
    }

    public ValueTask<BackendTrafficSnapshot?> CollectTrafficAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<BackendTrafficSnapshot?>(null);
    }

    private static BackendValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, Array.Empty<int>(), Array.Empty<int>(), errorCode, errorMessage);

    private static bool TryParseConfig(string configJson, out XrayConfig config)
    {
        config = null!;
        if (string.IsNullOrWhiteSpace(configJson)) return false;

        try
        {
            config = JsonSerializer.Deserialize(configJson, XrayJsonSerializerContext.Default.XrayConfig)!;
            return config is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidConfig(XrayConfig config) =>
        IsCanonicalIpAddress(config.ListenHost) &&
        config.ListenPort is >= 1 and <= 65_535 &&
        IsCanonicalGuid(config.ClientId) &&
        IsPrintableAscii(config.ClientEmail, 1, 128) &&
        config.Flow == VisionFlow &&
        IsBase64Url32Bytes(config.RealityPrivateKey) &&
        IsBase64Url32Bytes(config.RealityPublicKey) &&
        IsShortId(config.ShortId) &&
        IsDnsHostname(config.ServerName) &&
        IsDestination(config.Destination) &&
        config.Fingerprint is "chrome" or "firefox" or "safari" or "edge" or "randomized";

    private static bool IsCanonicalIpAddress(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IPAddress.TryParse(value, out var address)) return false;
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => string.Equals(value, address.ToString(), StringComparison.Ordinal),
            AddressFamily.InterNetworkV6 => string.Equals(value, address.ToString(), StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool IsPrintableAscii(string? value, int minimumLength, int maximumLength)
    {
        if (value is null || value.Length < minimumLength || value.Length > maximumLength) return false;
        foreach (var character in value)
        {
            if (character is < ' ' or > '~') return false;
        }

        return true;
    }

    private static bool IsCanonicalGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var guid) &&
        string.Equals(value, guid.ToString("D"), StringComparison.Ordinal);

    private static bool IsBase64Url32Bytes(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('=')) return false;
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')) return false;
        }

        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            return Convert.FromBase64String(padded).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsShortId(string? value)
    {
        if (value is null || value.Length is < 2 or > 16 || value.Length % 2 != 0) return false;
        foreach (var character in value)
        {
            if (!((character is >= '0' and <= '9') || (character is >= 'a' and <= 'f'))) return false;
        }

        return true;
    }

    private static bool IsDnsHostname(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253 ||
            value.EndsWith(".", StringComparison.Ordinal)) return false;
        var labels = value.Split('.');
        foreach (var label in labels)
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-') return false;
            foreach (var character in label)
            {
                if (!(char.IsAsciiLetterOrDigit(character) || character == '-')) return false;
            }
        }

        return true;
    }

    private static bool IsDestination(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        string host;
        string portText;
        if (value[0] == '[')
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket < 2 || closingBracket + 1 >= value.Length || value[closingBracket + 1] != ':')
                return false;
            host = value[1..closingBracket];
            portText = value[(closingBracket + 2)..];
            if (!IsCanonicalIpAddress(host) || !IPAddress.TryParse(host, out var address) ||
                address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        }
        else
        {
            var separator = value.LastIndexOf(':');
            if (separator < 1 || separator != value.IndexOf(':')) return false;
            host = value[..separator];
            portText = value[(separator + 1)..];
            if (!(IsCanonicalIpAddress(host) || IsDnsHostname(host))) return false;
        }

        return int.TryParse(portText, out var port) && port is >= 1 and <= 65_535 && portText == port.ToString();
    }

    private static XrayRuntimeConfig CreateRuntimeConfig(XrayConfig config) => new()
    {
        Log = new XrayLog { LogLevel = "warning" },
        Inbounds =
        [
            new XrayInbound
            {
                Listen = config.ListenHost!,
                Port = config.ListenPort,
                Protocol = "vless",
                Settings = new XrayInboundSettings
                {
                    Clients =
                    [
                        new XrayClient { Id = config.ClientId!, Email = config.ClientEmail!, Flow = config.Flow! }
                    ],
                    Decryption = "none"
                },
                StreamSettings = new XrayStreamSettings
                {
                    Network = "tcp",
                    Security = "reality",
                    RealitySettings = new XrayRealitySettings
                    {
                        Show = false,
                        Dest = config.Destination!,
                        Xver = 0,
                        ServerNames = [config.ServerName!],
                        PrivateKey = config.RealityPrivateKey!,
                        ShortIds = [config.ShortId!]
                    }
                }
            }
        ],
        Outbounds =
        [
            new XrayOutbound { Protocol = "freedom", Tag = "direct" },
            new XrayOutbound { Protocol = "blackhole", Tag = "blocked" }
        ]
    };
}