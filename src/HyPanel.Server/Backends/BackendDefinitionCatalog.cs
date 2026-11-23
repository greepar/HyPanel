using HyPanel.Server.Security;

namespace HyPanel.Server.Backends;

internal sealed record BackendFieldDefinition(
    string Key,
    string? ConfigKey,
    string Label,
    string Kind,
    bool Required,
    bool Secret,
    bool Generate,
    string? DefaultValue,
    string? Placeholder,
    string[] Options,
    string? Fixed,
    string? FixedKind,
    double? Min,
    double? Max,
    string Section);

/// <summary>
/// A backend is a protocol served by an engine: <see cref="BackendType"/> is what services store and Agents dispatch on,
/// <see cref="Engine"/> names the program (and its released binary) that runs on the node, and
/// <see cref="ServiceProtocol"/> is what clients speak. The same protocol can be offered by several engines.
/// </summary>
internal sealed record BackendDefinition(
    string BackendType,
    string DisplayName,
    string Core,
    string Protocol,
    string Description,
    string Badge,
    string FallbackVersion,
    BackendFieldDefinition[] Fields,
    string Engine,
    ServiceProtocol ServiceProtocol,
    BackendFeatures Features,
    Func<Dictionary<string, string>> GenerateDefaults,
    string? MinimumAgentVersion = null)
{
    public bool Has(BackendFeatures feature) => (Features & feature) == feature;
}

/// <summary>
/// Compile-time source of truth for backends: the service editor's fields (the frontend renders them, so adding or
/// changing fields never needs a frontend release), the engine, the client protocol and the features. Field keys match
/// each provider's config JSON exactly. To add a backend, add one entry here (and a <see cref="ServiceProtocol"/> if
/// clients speak something new) plus the Agent provider for its engine.
/// </summary>
internal static class BackendDefinitionCatalog
{
    private static readonly string[] Fingerprints =
        ["chrome", "firefox", "safari", "ios", "android", "edge", "random"];

    public static IReadOnlyList<BackendDefinition> All { get; } =
    [
        new("hysteria2", "Hysteria 2 官方服务端", "Hysteria 2", "Hysteria 2 / QUIC", "适合高延迟或不稳定网络。", "HY2",
            "2.12.2", Hysteria2Fields(),
            "hysteria2", Hysteria2Protocol.Instance,
            BackendFeatures.MultiUser | BackendFeatures.ControlPort | BackendFeatures.Certificate |
            BackendFeatures.PortHopping | BackendFeatures.AcmeCertificate,
            Hysteria2Defaults),
        // The same protocol served by Xray: clients see no difference, and Xray can route users, so one port carries
        // several lines with their own exits. Xray cannot issue ACME certificates itself.
        new("xray-hysteria2", "Hysteria 2 · Xray 内核", "Xray-core", "Hysteria 2 / QUIC",
            "与官方服务端兼容，并支持一个端口多条线路。", "HY2", "26.3.27", Hysteria2Fields(),
            "xray", Hysteria2Protocol.Instance,
            BackendFeatures.MultiUser | BackendFeatures.ControlPort | BackendFeatures.Certificate |
            BackendFeatures.PortHopping | BackendFeatures.Routes,
            Hysteria2Defaults, MinimumAgentVersion: "0.5.34"),
        new("xray", "Xray REALITY", "Xray-core", "VLESS + TCP + REALITY", "VLESS Vision 与 REALITY 握手。", "XR",
            "26.3.27",
            RealityFields(),
            "xray", VlessRealityProtocol.Instance,
            BackendFeatures.MultiUser | BackendFeatures.ControlPort | BackendFeatures.Routes | BackendFeatures.Relay,
            RealityDefaults),
        // VLESS + REALITY on Mihomo: the same fields, so a service switches between Xray and Mihomo freely.
        new("mihomo-reality", "REALITY · Mihomo 内核", "Mihomo", "VLESS + TCP + REALITY",
            "与 Xray 版本完全兼容，订阅不变，同样支持一个端口多条线路。", "XR", "1.19.34", RealityFields(),
            "mihomo", VlessRealityProtocol.Instance,
            BackendFeatures.MultiUser | BackendFeatures.ControlPort | BackendFeatures.Routes | BackendFeatures.Relay,
            RealityDefaults, MinimumAgentVersion: "0.5.36"),
        new("mihomo-anytls", "AnyTLS · Mihomo 内核", "Mihomo", "AnyTLS",
            "普通 TLS 外观并填充流量特征，需要一张证书。", "AT", "1.19.34",
            [
                .. Listen("443"),
                Field("certificateId", "证书", "certificate", required: true)
            ],
            "mihomo", AnyTlsProtocol.Instance,
            BackendFeatures.MultiUser | BackendFeatures.ControlPort | BackendFeatures.Certificate | BackendFeatures.Routes |
            BackendFeatures.Relay,
            static () => [], MinimumAgentVersion: "0.5.36"),
        new("mihomo-ss2022", "SS2022 + sing-mux · Mihomo 内核", "Mihomo", "Shadowsocks 2022",
            "Shadowsocks 2022 加密，客户端用 sing-mux 复用连接；TCP 与 UDP 同端口，无需证书。", "SS", "1.19.34",
            [
                .. Listen("8388"),
                Field("serverKey", "服务端密钥", "password", required: true, secret: true, generate: true),
                FixedField("cipher", "加密方式", HyPanel.Shared.Networking.Shadowsocks2022.Cipher, "string")
            ],
            "mihomo", Shadowsocks2022Protocol.Instance,
            BackendFeatures.MultiUser | BackendFeatures.ControlPort | BackendFeatures.Routes,
            static () => new Dictionary<string, string> { ["serverKey"] = SecretGenerator.StandardBase64(16) },
            MinimumAgentVersion: "0.5.36")
    ];

    public static bool TryGet(string? backendType, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BackendDefinition? definition)
    {
        definition = All.FirstOrDefault(item => string.Equals(item.BackendType, backendType, StringComparison.Ordinal));
        return definition is not null;
    }

    /// <summary>True when the backend exists and has every flag in <paramref name="feature"/>.</summary>
    public static bool Has(string? backendType, BackendFeatures feature) =>
        TryGet(backendType, out var definition) && definition.Has(feature);

    /// <summary>The backend types that have <paramref name="feature"/>, as an SQL <c>IN</c> list.</summary>
    public static string SqlList(BackendFeatures feature) =>
        "(" + string.Join(',', All.Where(item => item.Has(feature)).Select(item => $"'{item.BackendType}'")) + ")";

    public static Dictionary<string, string> GenerateDefaults(string backendType) =>
        TryGet(backendType, out var definition) ? definition.GenerateDefaults() : [];

    private static BackendFieldDefinition[] RealityFields() =>
    [
        .. Listen("443"),
        Field("realityPrivateKey", "REALITY 私钥", "password", required: true, secret: true, generate: true),
        Field("realityPublicKey", "REALITY 公钥", "text", required: true, generate: true),
        Field("shortId", "Short ID", "text", required: true, generate: true),
        Field("serverName", "服务器名称 / SNI", "text", required: true, generate: true,
            defaultValue: "www.cloudflare.com"),
        Field("destination", "伪装目标", "text", required: true, generate: true,
            defaultValue: "www.cloudflare.com:443"),
        Field("fingerprint", "浏览器指纹", "select", required: true, defaultValue: "chrome",
            options: Fingerprints),
        FixedField("flow", "流控", "xtls-rprx-vision", "string")
    ];

    private static BackendFieldDefinition[] Hysteria2Fields() =>
    [
        .. Listen("443"),
        Field("certificateId", "证书", "certificate", required: true),
        Field("authPassword", "认证密码", "password", required: true, secret: true, generate: true),
        Field("masqueradeUrl", "伪装地址", "text", required: true, defaultValue: "https://example.com/"),
        Field("obfsPassword", "Salamander 混淆密码", "password", secret: true),
        Field("upMbps", "上行 Mbps", "number", required: true, defaultValue: "100", min: 1),
        Field("downMbps", "下行 Mbps", "number", required: true, defaultValue: "1000", min: 1),
        Field("portHopping", "端口跳跃（可选）", "text",
            placeholder: "如 20000-30000 或 20000,20005,21000-21100")
    ];

    private static Dictionary<string, string> Hysteria2Defaults() => new()
    {
        ["authPassword"] = SecretGenerator.Base64Url(24),
        ["masqueradeUrl"] = MasqueradeSites[System.Security.Cryptography.RandomNumberGenerator.GetInt32(MasqueradeSites.Length)]
    };

    /// <summary>
    /// Built-in REALITY camouflage candidates. Reachability depends on the node and client networks.
    /// </summary>
    internal static readonly string[] RealityTargets =
    [
        "www.cloudflare.com", "www.visa.cn", "www.nvidia.com", "www.amd.com", "addons.mozilla.org",
        "www.tesla.com", "dl.google.com"
    ];

    /// <summary>Hysteria2 masquerade upstreams: real, stable HTTPS sites that non-proxy probes are shown.</summary>
    internal static readonly string[] MasqueradeSites =
    [
        "https://www.bing.com/", "https://www.apple.com/", "https://www.cloudflare.com/", "https://www.mozilla.org/",
        "https://www.wikipedia.org/", "https://www.nvidia.com/"
    ];

    private static Dictionary<string, string> RealityDefaults()
    {
        var (privateKey, publicKey) = SecretGenerator.RealityKeyPair();
        var target = RealityTargets[System.Security.Cryptography.RandomNumberGenerator.GetInt32(RealityTargets.Length)];
        return new Dictionary<string, string>
        {
            ["realityPrivateKey"] = privateKey,
            ["realityPublicKey"] = publicKey,
            ["shortId"] = SecretGenerator.LowerHex(8),
            ["serverName"] = target,
            ["destination"] = target + ":443"
        };
    }

    private static BackendFieldDefinition[] Listen(string port) =>
    [
        Field("listenHost", "监听地址", "text", required: true, defaultValue: "0.0.0.0", section: "listen"),
        Field("listenPort", "监听端口", "number", required: true, defaultValue: port, min: 1, max: 65535,
            section: "listen")
    ];

    private static BackendFieldDefinition Field(string key, string label, string kind, bool required = false,
        bool secret = false, bool generate = false, string? defaultValue = null, string[]? options = null,
        double? min = null, double? max = null, string section = "protocol", string? placeholder = null) =>
        new(key, key, label, kind, required, secret, generate, defaultValue, placeholder, options ?? [], null, null, min,
            max, section);

    private static BackendFieldDefinition FixedField(string key, string label, string value, string kind) =>
        new(key, key, label, "fixed", true, false, false, null, null, [], value, kind, null, null, "protocol");
}
