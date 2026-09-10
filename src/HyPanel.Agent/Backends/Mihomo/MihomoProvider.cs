namespace HyPanel.Agent.Backends.Mihomo;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Shared.Contracts;

public sealed class MihomoProvider : IBackendProvider
{
    private const string Shadowsocks2022Method = "2022-blake3-aes-256-gcm";

    public string BackendType => "mihomo";

    public BackendCapabilities Capabilities =>
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
            return ValueTask.FromResult(Invalid("unsupported_schema", "Unsupported Mihomo configuration schema."));
        }

        if (!TryParseConfig(desiredState.ConfigJson, out var config) || !IsValidConfig(config))
        {
            return ValueTask.FromResult(Invalid("invalid_config", "Invalid Mihomo configuration."));
        }

        return ValueTask.FromResult(new BackendValidationResult(
            true,
            [config.ListenPort],
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
            throw new InvalidOperationException("Mihomo configuration is invalid.");
        }

        var content = Encoding.UTF8.GetBytes(RenderYaml(config));
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
            ["-f", instance.ConfigPath],
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
                "Mihomo instance files are missing."));
        }

        return ValueTask.FromResult(new BackendHealthResult(true, null, null));
    }

    public ValueTask<BackendTrafficSnapshot?> CollectTrafficAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<BackendTrafficSnapshot?>(null);
    }

    private static BackendValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, Array.Empty<int>(), Array.Empty<int>(), errorCode, errorMessage);

    private static bool TryParseConfig(string configJson, out MihomoConfig config)
    {
        config = null!;

        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        try
        {
            config = JsonSerializer.Deserialize(configJson, MihomoJsonSerializerContext.Default.MihomoConfig)!;
            return config is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidConfig(MihomoConfig config) =>
        IsCanonicalIpAddress(config.ListenHost) &&
        config.ListenPort is >= 1 and <= 65_535 &&
        config.Method == Shadowsocks2022Method &&
        IsCanonicalBase64Secret(config.Password) &&
        config.Udp;

    private static bool IsCanonicalIpAddress(string? value)
    {
        if (string.IsNullOrEmpty(value) || !IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => string.Equals(value, address.ToString(), StringComparison.Ordinal),
            AddressFamily.InterNetworkV6 => string.Equals(value, address.ToString(), StringComparison.Ordinal),
            _ => false
        };
    }

    private static bool IsCanonicalBase64Secret(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            var decoded = Convert.FromBase64String(value);
            return decoded.Length == 32 &&
                   string.Equals(Convert.ToBase64String(decoded), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string RenderYaml(MihomoConfig config)
    {
        var yaml = new StringBuilder();
        yaml.AppendLine("log-level: warning");
        yaml.AppendLine("listeners:");
        yaml.Append("  - name: ").Append(QuoteYaml("hypanel-in")).AppendLine();
        yaml.Append("    type: ").Append(QuoteYaml("shadowsocks")).AppendLine();
        yaml.Append("    port: ").Append(config.ListenPort).AppendLine();
        yaml.Append("    listen: ").Append(QuoteYaml(config.ListenHost!)).AppendLine();
        yaml.Append("    cipher: ").Append(QuoteYaml(config.Method!)).AppendLine();
        yaml.Append("    password: ").Append(QuoteYaml(config.Password!)).AppendLine();
        yaml.AppendLine("    udp: true");
        yaml.AppendLine("rules:");
        yaml.Append("  - ").Append(QuoteYaml("MATCH,DIRECT")).AppendLine();
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