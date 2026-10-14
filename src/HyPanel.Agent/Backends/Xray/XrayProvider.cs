namespace HyPanel.Agent.Backends.Xray;

using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Shared.Contracts;

public sealed class XrayProvider : IBackendProvider
{
    private const string VisionFlow = "xtls-rprx-vision";
    private const int MaximumStatsOutputBytes = 1024 * 1024;
    private readonly TimeProvider timeProvider;

    public XrayProvider(TimeProvider timeProvider) => this.timeProvider = timeProvider;

    public string BackendType => "xray";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.MultiUser |
        BackendCapabilities.PerUserTraffic |
        BackendCapabilities.TrafficStats |
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

        if (!TryGetUsers(desiredState, out _))
            return ValueTask.FromResult(Invalid("invalid_users", "Invalid Xray users."));

        if (desiredState.ControlPort is not (>= 1024 and <= 65_535) || desiredState.ControlPort == config.ListenPort)
            return ValueTask.FromResult(Invalid("invalid_control_port", "Invalid Xray control port."));
        return ValueTask.FromResult(new BackendValidationResult(true, [config.ListenPort, desiredState.ControlPort.Value], Array.Empty<int>(), null,
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

        if (!TryGetUsers(desiredState, out var users))
            throw new InvalidOperationException("Xray users are invalid.");
        var content = JsonSerializer.SerializeToUtf8Bytes(CreateRuntimeConfig(desiredState.ControlPort!.Value, config, users),
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

    public async ValueTask<IReadOnlyList<BackendUserTraffic>> CollectUserTrafficAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(instance.BinaryPath)
        {
            WorkingDirectory = instance.InstanceDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("api");
        start.ArgumentList.Add("statsquery");
        if (instance.DesiredState.ControlPort is null) return [];
        start.ArgumentList.Add($"--server=127.0.0.1:{instance.DesiredState.ControlPort.Value}");
        start.ArgumentList.Add("--pattern=user>>>");
        using var process = new Process { StartInfo = start };
        if (!process.Start()) return [];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var outputTask = ReadBoundedAsync(process.StandardOutput, MaximumStatsOutputBytes, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, 4096, timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = await outputTask;
            _ = await errorTask;
            if (process.ExitCode != 0 || output is null) return [];
            return ParseStats(output, instance.DesiredState.Users, timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return [];
        }
        catch
        {
            TryKill(process);
            throw;
        }
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

    private static XrayRuntimeConfig CreateRuntimeConfig(int controlPort, XrayConfig config,
        IReadOnlyList<BackendUser> users) => new()
    {
        Log = new XrayLog { LogLevel = "warning" },
        Api = new XrayApi { Tag = "api", Listen = $"127.0.0.1:{controlPort}", Services = ["StatsService"] },
        Stats = new XrayStats(),
        Policy = new XrayPolicy
        {
            Levels = new Dictionary<string, XrayLevelPolicy>
            {
                ["0"] = new XrayLevelPolicy { StatsUserUplink = true, StatsUserDownlink = true }
            }
        },
        Inbounds =
        [
            new XrayInbound
            {
                Listen = config.ListenHost!,
                Port = config.ListenPort,
                Protocol = "vless",
                Settings = new XrayInboundSettings
                {
                    Clients = users.Select(user => new XrayClient
                    {
                        Id = user.Credential,
                        Email = UserEmail(user.UserId),
                        Flow = config.Flow!,
                        Level = 0
                    }).ToArray(),
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

    internal static IReadOnlyList<BackendUserTraffic> ParseStats(string json, IReadOnlyList<BackendUser>? desiredUsers,
        DateTimeOffset observedAt)
    {
        if (desiredUsers is null) return [];
        XrayStatsQueryResult? result;
        try
        {
            result = JsonSerializer.Deserialize(json, XrayStatsJsonSerializerContext.Default.XrayStatsQueryResult);
        }
        catch (JsonException)
        {
            return [];
        }
        var allowed = desiredUsers.Select(user => user.UserId).ToHashSet();
        var counters = new Dictionary<Guid, (long Upload, long Download)>();
        foreach (var stat in result?.Stat ?? [])
        {
            if (stat.Value < 0 || !TryParseStatName(stat.Name, out var userId, out var upload) || !allowed.Contains(userId))
                continue;
            counters.TryGetValue(userId, out var current);
            counters[userId] = upload ? (stat.Value, current.Download) : (current.Upload, stat.Value);
        }
        return counters.OrderBy(pair => pair.Key)
            .Select(pair => new BackendUserTraffic(pair.Key, pair.Value.Upload, pair.Value.Download, observedAt)).ToArray();
    }

    private static bool TryGetUsers(ServiceDesiredState desired, out IReadOnlyList<BackendUser> users)
    {
        users = desired.Users ?? [];
        var ids = new HashSet<Guid>();
        var credentials = new HashSet<string>(StringComparer.Ordinal);
        return users.Count <= 256 && users.All(user => user.UserId != Guid.Empty && ids.Add(user.UserId)
            && IsCanonicalGuid(user.Credential) && credentials.Add(user.Credential));
    }

    private static string UserEmail(Guid userId) => $"hypanel-{userId:N}";

    private static bool TryParseStatName(string? name, out Guid userId, out bool upload)
    {
        userId = Guid.Empty;
        upload = false;
        const string prefix = "user>>>hypanel-";
        const string middle = ">>>traffic>>>";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var separator = name.IndexOf(middle, prefix.Length, StringComparison.Ordinal);
        if (separator < 0 || !Guid.TryParseExact(name.AsSpan(prefix.Length, separator - prefix.Length), "N", out userId))
            return false;
        var direction = name[(separator + middle.Length)..];
        upload = direction == "uplink";
        return upload || direction == "downlink";
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        var bytes = 0;
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return builder.ToString();
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, read));
            if (bytes > maximumBytes) return null;
            builder.Append(buffer, 0, read);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}
