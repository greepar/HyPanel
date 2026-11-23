using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;

namespace HyPanel.Agent.Backends.Xray;

/// <summary>The settings every Xray-served protocol shares: where it listens, its default exit and its extra lines.</summary>
internal interface IXrayServiceConfig : IUpstreamProxyConfig
{
    string? ListenHost { get; }

    int ListenPort { get; }

    /// <summary>Elements are nullable: the config comes from the Panel as JSON and is validated, not trusted.</summary>
    XrayRouteConfig?[]? Routes { get; }

    /// <summary>Direct exits resolve and dial IPv4 only (the admin turned IPv6 egress off).</summary>
    bool? Ipv4Only { get; }
}

/// <summary>One user on one line: the secret they present and the email Xray keys stats and routing on.</summary>
internal readonly record struct XrayClientIdentity(Guid UserId, string Credential, string Email);

/// <summary>
/// Runs a protocol on Xray-core. The engine part is shared: one client per user and line, the default exit plus one
/// outbound per extra line with a routing rule sending that line's users to it, the stats API, the process and
/// per-user traffic. A protocol supplies only its config type, its validation and its inbound. To serve another
/// protocol with Xray, derive from this class and register the provider.
/// </summary>
internal abstract class XrayEngineProvider<TConfig>(TimeProvider timeProvider) : IBackendProvider
    where TConfig : class, IXrayServiceConfig
{
    private const int MaximumStatsOutputBytes = 1024 * 1024;

    public abstract string BackendType { get; }

    public BackendCapabilities Capabilities =>
        BackendCapabilities.MultiUser |
        BackendCapabilities.PerUserTraffic |
        BackendCapabilities.TrafficStats |
        BackendCapabilities.Logs |
        BackendCapabilities.VersionQuery |
        BackendCapabilities.ConfigValidation;

    /// <summary>Whether the protocol listens on TCP or UDP; the listen port is reported under it.</summary>
    protected abstract bool ListensOnUdp { get; }

    /// <summary>The config, or null when the JSON does not match the protocol's schema.</summary>
    protected abstract TConfig? Parse(string configJson);

    /// <summary>Protocol-specific checks; listen address, port, exit and lines are checked by the engine.</summary>
    protected abstract bool IsValidProtocolConfig(TConfig config, ServiceDesiredState desiredState);

    /// <summary>The protocol's inbound, admitting <paramref name="clients"/>.</summary>
    protected abstract JsonNode BuildInbound(TConfig config, IReadOnlyList<XrayClientIdentity> clients,
        ServiceDesiredState desiredState);

    public ValueTask<BackendValidationResult> ValidateAsync(ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (desiredState.ConfigSchemaVersion != 1)
            return ValueTask.FromResult(Invalid("unsupported_schema", "Unsupported Xray configuration schema."));
        if (!TryParse(desiredState.ConfigJson, out var config) || !IsValid(config, desiredState))
            return ValueTask.FromResult(Invalid("invalid_config", "Invalid Xray configuration."));
        if (!TryGetUsers(desiredState, out _))
            return ValueTask.FromResult(Invalid("invalid_users", "Invalid Xray users."));
        if (desiredState.ControlPort is not (>= 1024 and <= 65_535) || desiredState.ControlPort == config.ListenPort)
            return ValueTask.FromResult(Invalid("invalid_control_port", "Invalid Xray control port."));
        int[] tcp = ListensOnUdp ? [desiredState.ControlPort.Value] : [config.ListenPort, desiredState.ControlPort.Value];
        int[] udp = ListensOnUdp ? [config.ListenPort] : [];
        return ValueTask.FromResult(new BackendValidationResult(true, tcp, udp, null, null));
    }

    public ValueTask<RenderedBackendConfig> RenderConfigAsync(ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (desiredState.ConfigSchemaVersion != 1 || !TryParse(desiredState.ConfigJson, out var config) ||
            !IsValid(config, desiredState))
            throw new InvalidOperationException("Xray configuration is invalid.");
        if (!TryGetUsers(desiredState, out var users))
            throw new InvalidOperationException("Xray users are invalid.");

        var routes = config.Routes?.OfType<XrayRouteConfig>().ToArray() ?? [];
        // Every user gets one client per line: the default line keeps their credential, each route a derived one.
        var clients = users.SelectMany(user => routes
                .Select(route => new XrayClientIdentity(user.UserId, ServiceRoute.Credential(user.Credential, route.Id),
                    ServiceRoute.UserTag(user.UserId, route.Id)))
                .Prepend(new XrayClientIdentity(user.UserId, user.Credential, ServiceRoute.UserTag(user.UserId))))
            .ToArray();
        var ipv4Only = config.Ipv4Only == true;
        var outbounds = XrayOutbound.Create(config.ProxyOrNull(), ipv4Only).ToList();
        var rules = new List<XrayRoutingRule>();
        foreach (var route in routes)
        {
            var tag = $"route-{route.Id:N}";
            // Before the trailing blackhole, which stays last.
            outbounds.Insert(outbounds.Count - 1, XrayOutbound.For(route.ProxyOrNull(), tag, ipv4Only));
            if (users.Count > 0)
                rules.Add(new XrayRoutingRule
                {
                    Type = "field", OutboundTag = tag,
                    User = [.. users.Select(user => ServiceRoute.UserTag(user.UserId, route.Id))]
                });
        }

        var runtime = new XrayRuntimeConfig
        {
            Log = new XrayLog { LogLevel = "warning" },
            Api = new XrayApi { Tag = "api", Listen = $"127.0.0.1:{desiredState.ControlPort!.Value}", Services = ["StatsService"] },
            Stats = new XrayStats(),
            Policy = new XrayPolicy
            {
                Levels = new Dictionary<string, XrayLevelPolicy>
                {
                    ["0"] = new() { StatsUserUplink = true, StatsUserDownlink = true }
                }
            },
            Inbounds = [BuildInbound(config, clients, desiredState)],
            Outbounds = [.. outbounds],
            Routing = rules.Count > 0 ? new XrayRouting { Rules = [.. rules] } : null
        };
        var content = JsonSerializer.SerializeToUtf8Bytes(runtime, XrayRuntimeJsonSerializerContext.Default.XrayRuntimeConfig);
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
        return ValueTask.FromResult(!Directory.Exists(instance.InstanceDirectory) || !File.Exists(instance.ConfigPath)
            ? new BackendHealthResult(false, "instance_files_missing", "Xray instance files are missing.")
            : new BackendHealthResult(true, null, null));
    }

    public async ValueTask<IReadOnlyList<BackendUserTraffic>> CollectUserTrafficAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        if (instance.DesiredState.ControlPort is null) return [];
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
        start.ArgumentList.Add($"--server=127.0.0.1:{instance.DesiredState.ControlPort.Value}");
        start.ArgumentList.Add("--pattern=user>>>");
        using var process = new Process();
        process.StartInfo = start;
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

    /// <summary>Per-user traffic from <c>xray api statsquery</c>; a user's lines add up.</summary>
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
            counters[userId] = upload
                ? (current.Upload + stat.Value, current.Download)
                : (current.Upload, current.Download + stat.Value);
        }

        return [.. counters.OrderBy(pair => pair.Key).Select(pair => new BackendUserTraffic(pair.Key, pair.Value.Upload, pair.Value.Download, observedAt))];
    }

    /// <summary>A typed part of the runtime config as a node, for protocols that build their inbound from classes.</summary>
    protected static JsonNode ToNode<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        JsonSerializer.SerializeToNode(value, typeInfo)
        ?? throw new InvalidOperationException("Xray inbound serialized to null.");

    protected static bool IsCanonicalIpAddress(string? value) =>
        !string.IsNullOrEmpty(value) && IPAddress.TryParse(value, out var address)
        && address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && string.Equals(value, address.ToString(), StringComparison.Ordinal);

    protected static bool IsDnsHostname(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253 || value.EndsWith('.')) return false;
        return value.Split('.').All(label => label.Length is >= 1 and <= 63 && label[0] != '-' && label[^1] != '-'
                                             && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private bool TryParse(string configJson, out TConfig config)
    {
        config = null!;
        if (string.IsNullOrWhiteSpace(configJson)) return false;
        try
        {
            var parsed = Parse(configJson);
            if (parsed is null) return false;
            config = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsValid(TConfig config, ServiceDesiredState desiredState) =>
        IsCanonicalIpAddress(config.ListenHost) &&
        config.ListenPort is >= 1 and <= 65_535 &&
        config.TryGetProxy(out _) &&
        IsValidRoutes(config.Routes) &&
        IsValidProtocolConfig(config, desiredState);

    private static bool IsValidRoutes(XrayRouteConfig?[]? routes)
    {
        if (routes is null) return true;
        if (routes.Length > ServiceRoute.MaximumRoutes) return false;
        var ids = new HashSet<Guid>();
        return routes.All(route => route is not null && route.Id != Guid.Empty && ids.Add(route.Id)
                                   && !string.IsNullOrWhiteSpace(route.Name)
                                   && route.Name.Length <= ServiceRoute.MaximumNameLength
                                   && route.TryGetProxy(out _));
    }

    private static bool TryGetUsers(ServiceDesiredState desired, out IReadOnlyList<BackendUser> users)
    {
        users = desired.Users ?? [];
        var ids = new HashSet<Guid>();
        var credentials = new HashSet<string>(StringComparer.Ordinal);
        return users.Count <= 256 && users.All(user => user.UserId != Guid.Empty && ids.Add(user.UserId)
            && Guid.TryParseExact(user.Credential, "D", out var guid)
            && string.Equals(user.Credential, guid.ToString("D"), StringComparison.Ordinal)
            && credentials.Add(user.Credential));
    }

    private static bool TryParseStatName(string? name, out Guid userId, out bool upload)
    {
        userId = Guid.Empty;
        upload = false;
        const string prefix = "user>>>hypanel-";
        const string middle = ">>>traffic>>>";
        if (name is null || !name.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var separator = name.IndexOf(middle, prefix.Length, StringComparison.Ordinal);
        // "hypanel-<user>" on the default line, "hypanel-<user>-<route>" on a route.
        var length = separator - prefix.Length;
        if (separator < 0 || length is not (32 or 65) || length == 65 && name[prefix.Length + 32] != '-'
            || !Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out userId))
            return false;
        var direction = name[(separator + middle.Length)..];
        upload = direction == "uplink";
        return upload || direction == "downlink";
    }

    private static BackendValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, [], [], errorCode, errorMessage);

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
