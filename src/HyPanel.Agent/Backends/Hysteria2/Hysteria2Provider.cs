namespace HyPanel.Agent.Backends.Hysteria2;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Shared.Contracts;

public sealed class Hysteria2Provider : IBackendProvider
{
    private const int MinimumSecretLength = 8;
    private const int MaximumSecretLength = 256;
    private const int MaximumBandwidthMbps = 100_000;

    public string BackendType => "hysteria2";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.TrafficStats |
        BackendCapabilities.Logs |
        BackendCapabilities.VersionQuery |
        BackendCapabilities.ConfigValidation;

    public ValueTask<BackendValidationResult> ValidateAsync(
        ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (desiredState.ConfigSchemaVersion != 1)
        {
            return ValueTask.FromResult(Invalid("unsupported_schema", "Unsupported Hysteria2 configuration schema."));
        }

        if (!TryParseConfig(desiredState.ConfigJson, out var config) || !IsValidConfig(config))
        {
            return ValueTask.FromResult(Invalid("invalid_config", "Invalid Hysteria2 configuration."));
        }

        return ValueTask.FromResult(new BackendValidationResult(
            true,
            Array.Empty<int>(),
            [config.ListenPort],
            null,
            null));
    }

    public ValueTask<RenderedBackendConfig> RenderConfigAsync(
        ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (desiredState.ConfigSchemaVersion != 1 ||
            !TryParseConfig(desiredState.ConfigJson, out var config) ||
            !IsValidConfig(config))
        {
            throw new InvalidOperationException("Hysteria2 configuration is invalid.");
        }

        var yaml = RenderYaml(config);
        var content = Encoding.UTF8.GetBytes(yaml);
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        return ValueTask.FromResult(new RenderedBackendConfig("config.yaml", content, sha256));
    }

    public ValueTask<BackendProcessSpec> CreateProcessSpecAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new BackendProcessSpec(
            instance.BinaryPath,
            ["server", "-c", instance.ConfigPath],
            instance.InstanceDirectory,
            new Dictionary<string, string>()));
    }

    public ValueTask<BackendHealthResult> CheckHealthAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(instance.InstanceDirectory) || !File.Exists(instance.ConfigPath))
        {
            return ValueTask.FromResult(new BackendHealthResult(
                false,
                "instance_files_missing",
                "Hysteria2 instance files are missing."));
        }

        // Process liveness is intentionally supervised outside of the provider.
        return ValueTask.FromResult(new BackendHealthResult(true, null, null));
    }

    public ValueTask<BackendTrafficSnapshot?> CollectTrafficAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Hysteria2 does not expose a reliable, per-instance traffic API for this provider.
        return ValueTask.FromResult<BackendTrafficSnapshot?>(null);
    }

    private static BackendValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, Array.Empty<int>(), Array.Empty<int>(), errorCode, errorMessage);

    private static bool TryParseConfig(string configJson, out Hysteria2Config config)
    {
        config = null!;

        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        try
        {
            config = JsonSerializer.Deserialize(configJson, Hysteria2JsonSerializerContext.Default.Hysteria2Config)!;
            return config is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidConfig(Hysteria2Config config) =>
        IsValidListenHost(config.ListenHost) &&
        config.ListenPort is >= 1 and <= 65_535 &&
        IsAbsolutePath(config.CertificatePath) &&
        IsAbsolutePath(config.PrivateKeyPath) &&
        !PathsEqual(config.CertificatePath!, config.PrivateKeyPath!) &&
        HasValidSecretLength(config.AuthPassword) &&
        IsAbsoluteHttpsUri(config.MasqueradeUrl) &&
        (config.ObfsPassword is null || HasValidSecretLength(config.ObfsPassword)) &&
        config.UpMbps is >= 1 and <= MaximumBandwidthMbps &&
        config.DownMbps is >= 1 and <= MaximumBandwidthMbps;

    private static bool IsValidListenHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (host.Contains(':'))
        {
            return IPAddress.TryParse(host, out var ipv6) && ipv6.AddressFamily == AddressFamily.InterNetworkV6;
        }

        return IsCanonicalIpv4(host);
    }

    private static bool IsCanonicalIpv4(string host)
    {
        var segments = host.Split('.');
        if (segments.Length != 4)
        {
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length is < 1 or > 3 ||
                (segment.Length > 1 && segment[0] == '0') ||
                !byte.TryParse(segment, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool HasValidSecretLength(string? value) =>
        value is { Length: >= MinimumSecretLength and <= MaximumSecretLength };

    private static bool IsAbsoluteHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrEmpty(uri.Host);

    private static string RenderYaml(Hysteria2Config config)
    {
        var listenAddress = config.ListenHost!.Contains(':')
            ? $"[{config.ListenHost}]:{config.ListenPort}"
            : $"{config.ListenHost}:{config.ListenPort}";

        var yaml = new StringBuilder();
        yaml.Append("listen: ").Append(QuoteYaml(listenAddress)).AppendLine();
        yaml.AppendLine("tls:");
        yaml.Append("  cert: ").Append(QuoteYaml(config.CertificatePath!)).AppendLine();
        yaml.Append("  key: ").Append(QuoteYaml(config.PrivateKeyPath!)).AppendLine();
        yaml.AppendLine("auth:");
        yaml.Append("  type: ").Append(QuoteYaml("password")).AppendLine();
        yaml.Append("  password: ").Append(QuoteYaml(config.AuthPassword!)).AppendLine();
        yaml.AppendLine("masquerade:");
        yaml.Append("  type: ").Append(QuoteYaml("proxy")).AppendLine();
        yaml.AppendLine("  proxy:");
        yaml.Append("    url: ").Append(QuoteYaml(config.MasqueradeUrl!)).AppendLine();
        yaml.AppendLine("    rewriteHost: true");
        yaml.AppendLine("bandwidth:");
        yaml.Append("  up: ").Append(QuoteYaml($"{config.UpMbps} mbps")).AppendLine();
        yaml.Append("  down: ").Append(QuoteYaml($"{config.DownMbps} mbps")).AppendLine();

        if (config.ObfsPassword is not null)
        {
            yaml.AppendLine("obfs:");
            yaml.Append("  type: ").Append(QuoteYaml("salamander")).AppendLine();
            yaml.AppendLine("  salamander:");
            yaml.Append("    password: ").Append(QuoteYaml(config.ObfsPassword)).AppendLine();
        }

        return yaml.ToString();
    }

    private static string QuoteYaml(string value)
    {
        var quoted = new StringBuilder(value.Length + 2);
        quoted.Append('"');

        foreach (var character in value)
        {
            switch (character)
            {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                case '\b': quoted.Append("\\b"); break;
                case '\f': quoted.Append("\\f"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\t': quoted.Append("\\t"); break;
                default:
                    if (character < ' ')
                    {
                        quoted.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        quoted.Append(character);
                    }

                    break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
