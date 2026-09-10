namespace HyPanel.Agent.Backends.SingBox;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Shared.Contracts;

public sealed class SingBoxProvider : IBackendProvider
{
    private const string ShadowsocksMethod = "2022-blake3-aes-256-gcm";

    public string BackendType => "sing-box";

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
            return ValueTask.FromResult(Invalid("unsupported_schema", "Unsupported sing-box configuration schema."));
        }

        if (!TryParseConfig(desiredState.ConfigJson, out var config) || !IsValidConfig(config))
        {
            return ValueTask.FromResult(Invalid("invalid_config", "Invalid sing-box configuration."));
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

        if (desiredState.ConfigSchemaVersion != 1 || !TryParseConfig(desiredState.ConfigJson, out var config) ||
            !IsValidConfig(config))
        {
            throw new InvalidOperationException("sing-box configuration is invalid.");
        }

        var content = JsonSerializer.SerializeToUtf8Bytes(
            CreateRuntimeConfig(config),
            SingBoxRuntimeJsonSerializerContext.Default.SingBoxRuntimeConfig);
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return ValueTask.FromResult(new RenderedBackendConfig("config.json", content, sha256));
    }

    public ValueTask<BackendProcessSpec> CreateProcessSpecAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new BackendProcessSpec(
            instance.BinaryPath,
            ["run", "-c", instance.ConfigPath],
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
                "sing-box instance files are missing."));
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

    private static bool TryParseConfig(string configJson, out SingBoxConfig config)
    {
        config = null!;
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        try
        {
            config = JsonSerializer.Deserialize(configJson, SingBoxJsonSerializerContext.Default.SingBoxConfig)!;
            return config is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidConfig(SingBoxConfig config) =>
        IsCanonicalIpAddress(config.ListenHost) &&
        config.ListenPort is >= 1 and <= 65_535 &&
        config.Method == ShadowsocksMethod &&
        IsCanonicalBase64Password(config.Password) &&
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

    private static bool IsCanonicalBase64Password(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            return bytes.Length == 32 && string.Equals(value, Convert.ToBase64String(bytes), StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static SingBoxRuntimeConfig CreateRuntimeConfig(SingBoxConfig config) => new()
    {
        Log = new SingBoxLog { Level = "warn" },
        Inbounds =
        [
            new SingBoxInbound
            {
                Type = "shadowsocks",
                Tag = "hypanel-in",
                Listen = config.ListenHost!,
                ListenPort = config.ListenPort,
                Method = config.Method!,
                Password = config.Password!
            }
        ],
        Outbounds =
        [
            new SingBoxOutbound { Type = "direct", Tag = "direct" },
            new SingBoxOutbound { Type = "block", Tag = "block" }
        ]
    };
}