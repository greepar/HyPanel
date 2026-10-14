namespace HyPanel.Agent.Backends.Xray;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Shared.Contracts;

/// <summary>
/// Shadowsocks 2022 (<c>2022-blake3-aes-128-gcm</c>) served by Xray with one key per granted user.
/// Clients authenticate with <c>serverKey:userKey</c>; per-user traffic uses the same Xray stats API as REALITY.
/// Each user key is the 16 bytes of the user's binding credential (a GUID), so no extra secret is stored.
/// </summary>
public sealed class XrayShadowsocksProvider(TimeProvider timeProvider) : IBackendProvider
{
    public const string Method = "2022-blake3-aes-128-gcm";
    private readonly XrayProvider xray = new(timeProvider);

    public string BackendType => "xray-ss";

    public BackendCapabilities Capabilities => xray.Capabilities;

    /// <summary>Derives the per-user Shadowsocks 2022 key from the binding credential (Server does the same).</summary>
    public static string UserKey(string credential) => Convert.ToBase64String(Guid.Parse(credential).ToByteArray());

    public ValueTask<BackendValidationResult> ValidateAsync(ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (desiredState.ConfigSchemaVersion != 1 || !TryParseConfig(desiredState.ConfigJson, out var config))
            return ValueTask.FromResult(Invalid("invalid_config", "Invalid Xray Shadowsocks configuration."));
        if (!TryGetUsers(desiredState, out _))
            return ValueTask.FromResult(Invalid("invalid_users", "Invalid Xray Shadowsocks users."));
        if (desiredState.ControlPort is not (>= 1024 and <= 65_535) || desiredState.ControlPort == config.ListenPort)
            return ValueTask.FromResult(Invalid("invalid_control_port", "Invalid Xray control port."));
        return ValueTask.FromResult(new BackendValidationResult(true, [config.ListenPort, desiredState.ControlPort.Value],
            [config.ListenPort], null, null));
    }

    public ValueTask<RenderedBackendConfig> RenderConfigAsync(ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (desiredState.ConfigSchemaVersion != 1 || !TryParseConfig(desiredState.ConfigJson, out var config) ||
            !TryGetUsers(desiredState, out var users) || desiredState.ControlPort is not { } controlPort)
            throw new InvalidOperationException("Xray Shadowsocks configuration is invalid.");
        var runtime = new XrayShadowsocksRuntimeConfig
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
                new XrayShadowsocksInbound
                {
                    Listen = config.ListenHost!,
                    Port = config.ListenPort,
                    Protocol = "shadowsocks",
                    Settings = new XrayShadowsocksSettings
                    {
                        Method = Method,
                        Password = config.Password!,
                        Network = "tcp,udp",
                        Clients = users.Select(user => new XrayShadowsocksClient
                        {
                            Password = UserKey(user.Credential),
                            Email = $"hypanel-{user.UserId:N}",
                            Level = 0
                        }).ToArray()
                    }
                }
            ],
            Outbounds =
            [
                new XrayOutbound { Protocol = "freedom", Tag = "direct" },
                new XrayOutbound { Protocol = "blackhole", Tag = "blocked" }
            ]
        };
        var content = JsonSerializer.SerializeToUtf8Bytes(runtime,
            XrayRuntimeJsonSerializerContext.Default.XrayShadowsocksRuntimeConfig);
        return ValueTask.FromResult(new RenderedBackendConfig("config.json", content,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
    }

    public ValueTask<BackendProcessSpec> CreateProcessSpecAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken) => xray.CreateProcessSpecAsync(instance, cancellationToken);

    public ValueTask<BackendHealthResult> CheckHealthAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken) => xray.CheckHealthAsync(instance, cancellationToken);

    public ValueTask<IReadOnlyList<BackendUserTraffic>> CollectUserTrafficAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken) => xray.CollectUserTrafficAsync(instance, cancellationToken);

    private static bool TryParseConfig(string json, out XrayShadowsocksConfig config)
    {
        config = null!;
        try
        {
            config = JsonSerializer.Deserialize(json, XrayJsonSerializerContext.Default.XrayShadowsocksConfig)!;
        }
        catch (JsonException)
        {
            return false;
        }
        return config is not null && IPAddress.TryParse(config.ListenHost, out _) &&
               config.ListenPort is >= 1 and <= 65_535 &&
               config.Method is null or Method && IsKey16(config.Password);
    }

    private static bool IsKey16(string? value)
    {
        if (string.IsNullOrEmpty(value)) return false;
        try { return Convert.FromBase64String(value).Length == 16; }
        catch (FormatException) { return false; }
    }

    private static bool TryGetUsers(ServiceDesiredState desired, out IReadOnlyList<BackendUser> users)
    {
        users = desired.Users ?? [];
        var ids = new HashSet<Guid>();
        return users.Count <= 256 && users.All(user => user.UserId != Guid.Empty && ids.Add(user.UserId) &&
                                                      Guid.TryParseExact(user.Credential, "D", out _));
    }

    private static BackendValidationResult Invalid(string code, string message) =>
        new(false, Array.Empty<int>(), Array.Empty<int>(), code, message);
}
