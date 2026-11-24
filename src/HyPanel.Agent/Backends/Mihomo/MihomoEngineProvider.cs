using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;
using HyPanel.Agent.Backends.Xray;

namespace HyPanel.Agent.Backends.Mihomo;

/// <summary>One login on one line: the user, the secret they present and the name Mihomo reports and routes them by.</summary>
internal readonly record struct MihomoIdentity(Guid UserId, string Credential, string Name);

/// <summary>
/// Runs a protocol on Mihomo (Clash.Meta) through its <c>listeners</c>. The engine part is shared: one inbound listener
/// with one identity per user (<c>hypanel-&lt;user&gt;</c>), the default exit, IPv4-only egress, the loopback REST
/// API and per-user traffic from its connection list. A protocol supplies its config type, validation and listener.
/// The config is written as JSON, which Mihomo reads as YAML.
/// </summary>
internal abstract class MihomoEngineProvider<TConfig>(HttpClient? httpClient, TimeProvider timeProvider)
    : IBackendProvider, IConfigTestingBackend where TConfig : class, IMihomoServiceConfig
{
    private const int MaximumUsers = 256;
    private const int MaximumApiResponseBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<Guid, MihomoConnectionWatcher> watchers = new();

    public abstract string BackendType { get; }

    public BackendCapabilities Capabilities =>
        BackendCapabilities.MultiUser |
        BackendCapabilities.PerUserTraffic |
        BackendCapabilities.TrafficStats |
        BackendCapabilities.Logs |
        BackendCapabilities.ConfigValidation;

    protected abstract TConfig? Parse(string configJson);

    /// <summary>Whether the listener also takes UDP on its port (reported for the firewall and port checks).</summary>
    protected virtual bool AlsoListensOnUdp => false;

    /// <summary>Protocol-specific checks; listen address, port and exit are checked by the engine.</summary>
    protected abstract bool IsValidProtocolConfig(TConfig config, ServiceDesiredState desiredState);

    /// <summary>
    /// The protocol's listener without <c>name</c>, <c>listen</c> and <c>port</c>, admitting <paramref name="identities"/>
    /// (each user once per line, under the line's derived credential).
    /// </summary>
    protected abstract JsonObject BuildListener(TConfig config, IReadOnlyList<MihomoIdentity> identities,
        ServiceDesiredState desiredState);

    /// <summary>Directories outside the instance directory the listener reads files from.</summary>
    protected virtual IEnumerable<string> SafePaths(ServiceDesiredState desiredState) => [];

    /// <summary>The identity Mihomo reports a user's connections under (<c>inboundUser</c>).</summary>
    internal static string UserName(Guid userId) => $"hypanel-{userId:N}";

    public ValueTask<BackendValidationResult> ValidateAsync(ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (desiredState.ConfigSchemaVersion != 1)
            return ValueTask.FromResult(Invalid("unsupported_schema", "Unsupported Mihomo configuration schema."));
        if (!TryParse(desiredState.ConfigJson, out var config) || !IsValid(config, desiredState))
            return ValueTask.FromResult(Invalid("invalid_config", "Invalid Mihomo configuration."));
        if (!TryGetUsers(desiredState, out _))
            return ValueTask.FromResult(Invalid("invalid_users", "Invalid Mihomo users."));
        if (desiredState.ControlPort is not (>= 1024 and <= 65_535) || desiredState.ControlPort == config.ListenPort)
            return ValueTask.FromResult(Invalid("invalid_control_port", "Invalid Mihomo control port."));
        return ValueTask.FromResult(new BackendValidationResult(true,
            [config.ListenPort, desiredState.ControlPort.Value], AlsoListensOnUdp ? [config.ListenPort] : [], null, null));
    }

    public ValueTask<RenderedBackendConfig> RenderConfigAsync(ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (desiredState.ConfigSchemaVersion != 1 || !TryParse(desiredState.ConfigJson, out var config) ||
            !IsValid(config, desiredState))
            throw new InvalidOperationException("Mihomo configuration is invalid.");
        if (!TryGetUsers(desiredState, out var users))
            throw new InvalidOperationException("Mihomo users are invalid.");

        var routes = config.Routes?.OfType<XrayRouteConfig>().ToArray() ?? [];
        var defaultProxied = config.ProxyOrNull() is not null;
        // Every user logs in once per line they may take: the default line with their credential, each route with a
        // derived one. A direct-only user has no login on a line that leaves through a proxy.
        var identities = users.SelectMany(user => (user.Admits(defaultProxied)
                    ? [new MihomoIdentity(user.UserId, user.Credential, ServiceRoute.UserTag(user.UserId))]
                    : Array.Empty<MihomoIdentity>())
                .Concat(routes.Where(route => user.Admits(route.ProxyOrNull() is not null))
                    .Select(route => new MihomoIdentity(user.UserId, ServiceRoute.Credential(user.Credential, route.Id),
                        ServiceRoute.UserTag(user.UserId, route.Id)))))
            .ToArray();
        var listener = BuildListener(config, identities, desiredState);
        listener["name"] = "hypanel-in";
        listener["listen"] = config.ListenHost;
        listener["port"] = config.ListenPort;
        var proxies = new JsonArray();
        var rules = new JsonArray();
        foreach (var route in routes)
        {
            // A line's users go to its own exit; a direct line goes out directly even when the default is a proxy.
            var target = "DIRECT";
            if (route.ProxyOrNull() is { } routeProxy)
            {
                target = $"route-{route.Id:N}";
                proxies.Add((JsonNode)Upstream(routeProxy, target));
            }
            var routeUsers = users.Where(user => user.Admits(route.ProxyOrNull() is not null))
                .Select(user => ServiceRoute.UserTag(user.UserId, route.Id)).ToArray();
            if (routeUsers.Length > 0)
                rules.Add((JsonNode)$"IN-USER,{string.Join('/', routeUsers)},{target}");
        }
        // Everything else leaves through the upstream proxy when there is one, failing closed like the other engines.
        var proxy = config.ProxyOrNull();
        if (proxy is not null) proxies.Insert(0, Upstream(proxy, "upstream"));
        rules.Add((JsonNode)(proxy is null ? "MATCH,DIRECT" : "MATCH,upstream"));
        var root = new JsonObject
        {
            ["mode"] = "rule",
            ["log-level"] = "warning",
            // Turning IPv6 off also stops resolving AAAA, so direct connections leave over IPv4 only.
            ["ipv6"] = config.Ipv4Only != true,
            ["external-controller"] = $"127.0.0.1:{desiredState.ControlPort!.Value}",
            ["secret"] = ApiSecret(desiredState),
            ["geo-auto-update"] = false,
            ["find-process-mode"] = "off",
            ["profile"] = new JsonObject { ["store-selected"] = false, ["store-fake-ip"] = false },
            ["listeners"] = new JsonArray(listener),
            ["rules"] = rules
        };
        if (proxies.Count > 0) root["proxies"] = proxies;
        var content = Encoding.UTF8.GetBytes(root.ToJsonString());
        var sha256 = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        return ValueTask.FromResult(new RenderedBackendConfig("config.yaml", content, sha256));
    }

    public ValueTask<BackendProcessSpec> CreateProcessSpecAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // -d keeps Mihomo's cache and any downloads inside the service's own directory. Mihomo reads files only from
        // there unless SAFE_PATHS lists more, so directories the protocol needs outside it (certificates) go there.
        var environment = new Dictionary<string, string>();
        var extra = SafePaths(instance.DesiredState).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToArray();
        if (extra.Length > 0) environment["SAFE_PATHS"] = string.Join(Path.PathSeparator, extra);
        return ValueTask.FromResult(new BackendProcessSpec(instance.BinaryPath,
            ["-d", instance.InstanceDirectory, "-f", instance.ConfigPath], instance.InstanceDirectory, environment));
    }

    public async ValueTask<string?> TestConfigAsync(BackendInstanceContext instance, CancellationToken cancellationToken)
    {
        // Same directory and SAFE_PATHS as the real run, so certificate paths are judged the same way.
        var spec = await CreateProcessSpecAsync(instance, cancellationToken);
        return await Infrastructure.ConfigTest.RunAsync(instance.BinaryPath, ["-t", .. spec.Arguments],
            instance.InstanceDirectory, spec.Environment, cancellationToken);
    }

    public ValueTask<BackendHealthResult> CheckHealthAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(!Directory.Exists(instance.InstanceDirectory) || !File.Exists(instance.ConfigPath)
            ? new BackendHealthResult(false, "instance_files_missing", "Mihomo instance files are missing.")
            : new BackendHealthResult(true, null, null));
    }

    public async ValueTask<IReadOnlyList<BackendUserTraffic>> CollectUserTrafficAsync(BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        var desired = instance.DesiredState;
        if (desired.ControlPort is not { } port || desired.Users is not { Count: > 0 } users) return [];
        // A user's lines all count for the user.
        var routeIds = TryParse(desired.ConfigJson, out var config)
            ? config.Routes?.OfType<XrayRouteConfig>().Select(route => route.Id).ToArray() ?? []
            : [];
        var byName = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var user in users)
        {
            byName[ServiceRoute.UserTag(user.UserId)] = user.UserId;
            foreach (var route in routeIds) byName[ServiceRoute.UserTag(user.UserId, route)] = user.UserId;
        }

        var watcher = Watcher(desired.ServiceId, port, ApiSecret(desired));
        watcher.Want(byName, users.Select(user => user.UserId).ToHashSet());
        // The stream does the counting; poll once only while it is not delivering (just started, or reconnecting).
        var totals = watcher.Observe(watcher.IsStreaming ? null : await PollAsync(port, watcher.Secret, cancellationToken));
        var observedAt = timeProvider.GetUtcNow();
        return [.. users.Where(user => totals.ContainsKey(user.UserId)).OrderBy(user => user.UserId)
            .Select(user => new BackendUserTraffic(user.UserId, totals[user.UserId].Upload,
                totals[user.UserId].Download, observedAt))];
    }

    /// <summary>The service's watcher, replaced when its control port or secret changed with the config.</summary>
    private MihomoConnectionWatcher Watcher(Guid serviceId, int port, string secret)
    {
        while (true)
        {
            if (watchers.TryGetValue(serviceId, out var current))
            {
                if (current.Port == port && current.Secret == secret) return current;
                if (watchers.TryRemove(new KeyValuePair<Guid, MihomoConnectionWatcher>(serviceId, current))) current.Dispose();
                continue;
            }
            var created = new MihomoConnectionWatcher(port, secret, timeProvider,
                stopped => watchers.TryRemove(new KeyValuePair<Guid, MihomoConnectionWatcher>(serviceId, stopped)));
            if (watchers.TryAdd(serviceId, created)) return created;
            created.Dispose();
        }
    }

    private async Task<MihomoConnections?> PollAsync(int port, string secret, CancellationToken cancellationToken)
    {
        if (httpClient is null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/connections");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {secret}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumApiResponseBytes)
                return null;
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return body.Length > MaximumApiResponseBytes
                ? null
                : JsonSerializer.Deserialize(body, MihomoApiJsonSerializerContext.Default.MihomoConnections);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
                                              or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Still starting; the next pass retries.
            return null;
        }
    }

    /// <summary>Stable per config, so an unchanged service renders the same file and is not restarted.</summary>
    private static string ApiSecret(ServiceDesiredState desired) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"hypanel-mihomo-api:{desired.ServiceId:N}:{desired.ConfigJson}")))
            .ToLowerInvariant()[..32];

    private static JsonObject Upstream(UpstreamProxy proxy, string name)
    {
        var node = new JsonObject
        {
            ["name"] = name,
            ["type"] = proxy.Type == UpstreamProxy.Socks5 ? "socks5" : "http",
            ["server"] = proxy.Host,
            ["port"] = proxy.Port,
            ["udp"] = proxy.Type == UpstreamProxy.Socks5
        };
        if (proxy.Type == UpstreamProxy.Https)
        {
            node["tls"] = true;
            node["sni"] = proxy.Host;
        }
        if (proxy.Username is not null)
        {
            node["username"] = proxy.Username;
            node["password"] = proxy.Password ?? string.Empty;
        }
        return node;
    }

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
        return users.Count <= MaximumUsers && users.All(user => user.UserId != Guid.Empty && ids.Add(user.UserId)
            && Guid.TryParseExact(user.Credential, "D", out var guid)
            && string.Equals(user.Credential, guid.ToString("D"), StringComparison.Ordinal)
            && credentials.Add(user.Credential));
    }

    private static BackendValidationResult Invalid(string errorCode, string errorMessage) =>
        new(false, [], [], errorCode, errorMessage);
}
