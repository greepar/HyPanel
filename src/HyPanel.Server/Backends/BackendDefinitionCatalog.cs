namespace HyPanel.Server.Backends;

using HyPanel.Server.Security;

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

internal sealed record BackendDefinition(
    string BackendType,
    string DisplayName,
    string Core,
    string Protocol,
    string Description,
    string Badge,
    string FallbackVersion,
    BackendFieldDefinition[] Fields);

/// <summary>
/// Compile-time source of truth for the service editor. The frontend renders these definitions, so adding or changing a
/// backend's fields never requires a frontend release. Field keys match each provider's config JSON exactly.
/// </summary>
internal static class BackendDefinitionCatalog
{
    private static readonly string[] Fingerprints =
        ["chrome", "firefox", "safari", "ios", "android", "edge", "random"];

    public static IReadOnlyList<BackendDefinition> All { get; } =
    [
        new("hysteria2", "Hysteria 2 官方服务端", "Hysteria 2", "Hysteria 2 / QUIC", "适合高延迟或不稳定网络。", "HY2",
            "2.12.2",
            [
                .. Listen("443"),
                Field("certificateId", "证书", "certificate", required: true),
                Field("authPassword", "认证密码", "password", required: true, secret: true, generate: true),
                Field("masqueradeUrl", "伪装地址", "text", required: true, defaultValue: "https://example.com/"),
                Field("obfsPassword", "Salamander 混淆密码", "password", secret: true, generate: true),
                Field("upMbps", "上行 Mbps", "number", required: true, defaultValue: "100", min: 1),
                Field("downMbps", "下行 Mbps", "number", required: true, defaultValue: "100", min: 1),
                Field("portHopping", "端口跳跃（可选）", "text",
                    placeholder: "如 20000-30000 或 20000,20005,21000-21100")
            ]),
        new("xray", "Xray REALITY", "Xray-core", "VLESS + TCP + REALITY", "VLESS Vision 与 REALITY 握手。", "XR",
            "26.3.27",
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
            ]),
        new("xray-ss", "Xray Shadowsocks", "Xray-core", "Shadowsocks 2022 多用户",
            "每个用户独立密钥与流量统计，支持 UDP。", "SS", "26.3.27",
            [
                .. Listen("8388"),
                Field("password", "服务端密钥", "password", required: true, secret: true, generate: true),
                FixedField("method", "加密方法", "2022-blake3-aes-128-gcm", "string"),
                FixedField("udp", "UDP", "true", "boolean")
            ])
    ];

    public static bool TryGet(string backendType, out BackendDefinition? definition)
    {
        definition = All.FirstOrDefault(item => string.Equals(item.BackendType, backendType, StringComparison.Ordinal));
        return definition is not null;
    }

    public static Dictionary<string, string> GenerateDefaults(string backendType) => backendType switch
    {
        "hysteria2" => new Dictionary<string, string>
        {
            ["authPassword"] = SecretGenerator.Base64Url(24),
            ["obfsPassword"] = SecretGenerator.Base64Url(24)
        },
        "xray" => RealityDefaults(),
        "xray-ss" => new Dictionary<string, string>
        {
            // 2022-blake3-aes-128-gcm uses a 16-byte key.
            ["password"] = SecretGenerator.StandardBase64(16)
        },
        _ => []
    };

    /// <summary>
    /// REALITY camouflage targets: large sites that serve TLS 1.3 + HTTP/2 and are reachable from mainland China.
    /// Microsoft endpoints are avoided because they frequently break REALITY handshakes.
    /// </summary>
    internal static readonly string[] RealityTargets =
    [
        "www.cloudflare.com", "www.visa.cn", "www.visa.com", "www.nvidia.com", "www.amd.com", "addons.mozilla.org",
        "www.tesla.com", "dl.google.com"
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
