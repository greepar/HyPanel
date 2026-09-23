namespace HyPanel.Agent.Backends.Hysteria2;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HyPanel.Agent.Backends;
using HyPanel.Shared.Contracts;

/// <summary>
/// Hysteria2 server. Granted users authenticate with distinct <c>userpass</c> identities
/// (<c>hypanel-&lt;userId:N&gt;</c> / per-binding credential) and their cumulative traffic is read from the
/// loopback-only trafficStats API on the Panel-assigned control port.
/// </summary>
public sealed class Hysteria2Provider(HttpClient? httpClient = null, TimeProvider? timeProvider = null) : IBackendProvider
{
    private const int MinimumSecretLength = 8;
    private const int MaximumSecretLength = 256;
    private const int MaximumBandwidthMbps = 100_000;
    private const int MaximumUsers = 256;
    private const int MaximumStatsResponseBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public string BackendType => "hysteria2";

    public BackendCapabilities Capabilities =>
        BackendCapabilities.MultiUser |
        BackendCapabilities.PerUserTraffic |
        BackendCapabilities.TrafficStats |
        BackendCapabilities.Logs |
        BackendCapabilities.VersionQuery |
        BackendCapabilities.ConfigValidation;

    internal static string UserName(Guid userId) => $"hypanel-{userId:N}";

    public ValueTask<BackendValidationResult> ValidateAsync(
        ServiceDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (desiredState.ConfigSchemaVersion != 1)
        {
            return ValueTask.FromResult(Invalid("unsupported_schema", "Unsupported Hysteria2 configuration schema."));
        }

        if (!TryParseConfig(desiredState.ConfigJson, out var config) || !IsValidConfig(config, desiredState))
        {
            return ValueTask.FromResult(Invalid("invalid_config", "Invalid Hysteria2 configuration."));
        }

        if (!TryGetUsers(desiredState, out _))
        {
            return ValueTask.FromResult(Invalid("invalid_users", "Invalid Hysteria2 user list."));
        }

        if (desiredState.ControlPort is { } controlPort && controlPort is not (>= 1024 and <= 65_535))
        {
            return ValueTask.FromResult(Invalid("invalid_control_port", "Invalid Hysteria2 control port."));
        }

        var tcpPorts = new List<int>();
        if (desiredState.ControlPort is { } port) tcpPorts.Add(port);
        if (desiredState.TlsCertificate is { Kind: TlsCertificateKinds.Acme } acme)
        {
            if (acme.AcmeChallenge == AcmeChallenges.Http) tcpPorts.Add(80);
            else if (acme.AcmeChallenge == AcmeChallenges.Tls) tcpPorts.Add(443);
        }

        return ValueTask.FromResult(new BackendValidationResult(
            true,
            tcpPorts,
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
            !IsValidConfig(config, desiredState))
        {
            throw new InvalidOperationException("Hysteria2 configuration is invalid.");
        }

        if (!TryGetUsers(desiredState, out var users))
        {
            throw new InvalidOperationException("Hysteria2 user list is invalid.");
        }

        var yaml = RenderYaml(config, desiredState.TlsCertificate, users, desiredState.ControlPort,
            StatsSecret(desiredState));
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

    public async ValueTask<IReadOnlyList<BackendUserTraffic>> CollectUserTrafficAsync(
        BackendInstanceContext instance,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var desired = instance.DesiredState;
        if (httpClient is null || desired.ControlPort is not { } port || desired.Users is not { Count: > 0 })
            return [];

        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/traffic");
        request.Headers.TryAddWithoutValidation("Authorization", StatsSecret(desired));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StatsTimeout);
        try
        {
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumStatsResponseBytes)
                return [];
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return body.Length > MaximumStatsResponseBytes ? [] : ParseTraffic(body, desired.Users, clock.GetUtcNow());
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
                                              && !cancellationToken.IsCancellationRequested)
        {
            // The backend may still be starting; the next reconciliation pass retries.
            return [];
        }
    }

    internal static IReadOnlyList<BackendUserTraffic> ParseTraffic(string json, IReadOnlyList<BackendUser> users,
        DateTimeOffset observedAt)
    {
        Dictionary<string, Hysteria2TrafficCounter>? counters;
        try
        {
            counters = JsonSerializer.Deserialize(json,
                Hysteria2TrafficJsonSerializerContext.Default.DictionaryStringHysteria2TrafficCounter);
        }
        catch (JsonException)
        {
            return [];
        }

        if (counters is null) return [];
        var result = new List<BackendUserTraffic>();
        foreach (var user in users.OrderBy(item => item.UserId))
        {
            if (!counters.TryGetValue(UserName(user.UserId), out var counter) || counter.Tx < 0 || counter.Rx < 0)
                continue;
            result.Add(new BackendUserTraffic(user.UserId, counter.Tx, counter.Rx, observedAt));
        }

        return result;
    }

    private static bool TryGetUsers(ServiceDesiredState desired, out IReadOnlyList<BackendUser> users)
    {
        users = desired.Users ?? [];
        var ids = new HashSet<Guid>();
        var credentials = new HashSet<string>(StringComparer.Ordinal);
        return users.Count <= MaximumUsers && users.All(user => user.UserId != Guid.Empty && ids.Add(user.UserId)
            && HasValidSecretLength(user.Credential) && !user.Credential.Any(char.IsControl)
            && credentials.Add(user.Credential));
    }

    /// <summary>Stable, service-scoped secret for the loopback-only trafficStats listener.</summary>
    private static string StatsSecret(ServiceDesiredState desired) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"hypanel-hy2-stats:{desired.ServiceId:N}:{desired.ConfigJson}")))
            .ToLowerInvariant()[..32];

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

    private static bool IsValidConfig(Hysteria2Config config, ServiceDesiredState desiredState) =>
        IsValidListenHost(config.ListenHost) &&
        config.ListenPort is >= 1 and <= 65_535 &&
        HasValidCertificate(config, desiredState) &&
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

    private static bool IsSha256(string value) => value.Length == 64 && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasValidCertificate(Hysteria2Config config, ServiceDesiredState desiredState)
    {
        if (config.CertificateId != Guid.Empty && desiredState.TlsCertificate is { } tls)
            return tls.CertificateId == config.CertificateId && tls.Kind switch
            {
                TlsCertificateKinds.Upload => IsSha256(tls.Fingerprint),
                TlsCertificateKinds.Path => IsAbsolutePath(tls.CertificatePath) && IsAbsolutePath(tls.PrivateKeyPath)
                                            && !PathsEqual(tls.CertificatePath!, tls.PrivateKeyPath!),
                TlsCertificateKinds.Acme => tls.AcmeDomains is { Count: > 0 } domains
                                            && domains.All(IsDnsName)
                                            && tls.AcmeEmail is { Length: > 3 } email && email.Contains('@')
                                            && tls.AcmeChallenge is AcmeChallenges.Http or AcmeChallenges.Tls
                                                or AcmeChallenges.Cloudflare
                                            && (tls.AcmeChallenge != AcmeChallenges.Cloudflare
                                                || !string.IsNullOrWhiteSpace(tls.AcmeDnsToken)),
                _ => false
            };
        return IsAbsolutePath(config.CertificatePath) && IsAbsolutePath(config.PrivateKeyPath) &&
               !PathsEqual(config.CertificatePath!, config.PrivateKeyPath!);
    }

    private static bool IsDnsName(string value) =>
        value.Length is > 0 and <= 253 && Uri.CheckHostName(value) == UriHostNameType.Dns && !value.Contains('*');

    private static bool IsAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool HasValidSecretLength(string? value) =>
        value is { Length: >= MinimumSecretLength and <= MaximumSecretLength };

    private static bool IsAbsoluteHttpsUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrEmpty(uri.Host);

    private static string RenderYaml(Hysteria2Config config, TlsCertificateAsset? tls,
        IReadOnlyList<BackendUser> users, int? controlPort, string statsSecret)
    {
        var listenAddress = config.ListenHost!.Contains(':')
            ? $"[{config.ListenHost}]:{config.ListenPort}"
            : $"{config.ListenHost}:{config.ListenPort}";

        var yaml = new StringBuilder();
        yaml.Append("listen: ").Append(QuoteYaml(listenAddress)).AppendLine();
        if (tls?.Kind == TlsCertificateKinds.Acme)
        {
            // Hysteria issues and renews the certificate itself; state lives in the instance directory.
            yaml.AppendLine("acme:");
            yaml.AppendLine("  domains:");
            foreach (var domain in tls.AcmeDomains!)
                yaml.Append("    - ").Append(QuoteYaml(domain)).AppendLine();
            yaml.Append("  email: ").Append(QuoteYaml(tls.AcmeEmail!)).AppendLine();
            yaml.AppendLine("  ca: \"letsencrypt\"");
            yaml.AppendLine("  dir: \"acme\"");
            if (tls.AcmeChallenge == AcmeChallenges.Cloudflare)
            {
                yaml.AppendLine("  type: \"dns\"");
                yaml.AppendLine("  dns:");
                yaml.AppendLine("    name: \"cloudflare\"");
                yaml.AppendLine("    config:");
                yaml.Append("      cloudflare_api_token: ").Append(QuoteYaml(tls.AcmeDnsToken!)).AppendLine();
            }
            else
            {
                yaml.Append("  type: ").Append(QuoteYaml(tls.AcmeChallenge!)).AppendLine();
            }
        }
        else
        {
            yaml.AppendLine("tls:");
            var (certificatePath, privateKeyPath) = tls?.Kind switch
            {
                TlsCertificateKinds.Path => (tls.CertificatePath!, tls.PrivateKeyPath!),
                TlsCertificateKinds.Upload => ($"tls/{tls.Fingerprint}/cert.pem", $"tls/{tls.Fingerprint}/key.pem"),
                _ => (config.CertificatePath!, config.PrivateKeyPath!)
            };
            yaml.Append("  cert: ").Append(QuoteYaml(certificatePath)).AppendLine();
            yaml.Append("  key: ").Append(QuoteYaml(privateKeyPath)).AppendLine();
        }
        yaml.AppendLine("auth:");
        if (users.Count > 0)
        {
            yaml.Append("  type: ").Append(QuoteYaml("userpass")).AppendLine();
            yaml.AppendLine("  userpass:");
            foreach (var user in users.OrderBy(item => item.UserId))
                yaml.Append("    ").Append(QuoteYaml(UserName(user.UserId))).Append(": ")
                    .Append(QuoteYaml(user.Credential)).AppendLine();
        }
        else
        {
            yaml.Append("  type: ").Append(QuoteYaml("password")).AppendLine();
            yaml.Append("  password: ").Append(QuoteYaml(config.AuthPassword!)).AppendLine();
        }

        if (controlPort is { } port)
        {
            yaml.AppendLine("trafficStats:");
            yaml.Append("  listen: ").Append(QuoteYaml($"127.0.0.1:{port}")).AppendLine();
            yaml.Append("  secret: ").Append(QuoteYaml(statsSecret)).AppendLine();
        }
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
