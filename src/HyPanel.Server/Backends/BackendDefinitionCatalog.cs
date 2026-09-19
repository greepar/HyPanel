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
                Field("downMbps", "下行 Mbps", "number", required: true, defaultValue: "100", min: 1)
            ]),
        new("xray", "Xray REALITY", "Xray-core", "VLESS + TCP + REALITY", "VLESS Vision 与 REALITY 握手。", "XR",
            "26.3.27",
            [
                .. Listen("443"),
                Field("realityPrivateKey", "REALITY 私钥", "password", required: true, secret: true, generate: true),
                Field("realityPublicKey", "REALITY 公钥", "text", required: true, generate: true),
                Field("shortId", "Short ID", "text", required: true, generate: true),
                Field("serverName", "服务器名称 / SNI", "text", required: true,
                    defaultValue: "www.microsoft.com"),
                Field("destination", "伪装目标", "text", required: true, defaultValue: "www.microsoft.com:443"),
                Field("fingerprint", "浏览器指纹", "select", required: true, defaultValue: "chrome",
                    options: Fingerprints),
                FixedField("flow", "流控", "xtls-rprx-vision", "string")
            ]),
        new("mihomo", "Mihomo Shadowsocks", "Mihomo", "Shadowsocks 2022", "Shadowsocks 2022 入站，支持 UDP。", "MI",
            "1.19.30",
            ShadowsocksFields("24446")),
        new("sing-box", "sing-box Shadowsocks", "sing-box", "Shadowsocks 2022", "Shadowsocks 2022 入站，支持 UDP。", "SB",
            "1.14.0",
            ShadowsocksFields("24447"))
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
        "mihomo" or "sing-box" => new Dictionary<string, string>
        {
            ["password"] = SecretGenerator.StandardBase64(32)
        },
        _ => []
    };

    private static Dictionary<string, string> RealityDefaults()
    {
        var (privateKey, publicKey) = SecretGenerator.RealityKeyPair();
        return new Dictionary<string, string>
        {
            ["realityPrivateKey"] = privateKey,
            ["realityPublicKey"] = publicKey,
            ["shortId"] = SecretGenerator.LowerHex(8)
        };
    }

    private static BackendFieldDefinition[] ShadowsocksFields(string port) =>
    [
        .. Listen(port),
        Field("password", "Shadowsocks 密钥", "password", required: true, secret: true, generate: true),
        FixedField("method", "加密方法", "2022-blake3-aes-256-gcm", "string"),
        FixedField("udp", "UDP", "true", "boolean")
    ];

    private static BackendFieldDefinition[] Listen(string port) =>
    [
        Field("listenHost", "监听地址", "text", required: true, defaultValue: "0.0.0.0", section: "listen"),
        Field("listenPort", "监听端口", "number", required: true, defaultValue: port, min: 1, max: 65535,
            section: "listen")
    ];

    private static BackendFieldDefinition Field(string key, string label, string kind, bool required = false,
        bool secret = false, bool generate = false, string? defaultValue = null, string[]? options = null,
        double? min = null, double? max = null, string section = "protocol") =>
        new(key, key, label, kind, required, secret, generate, defaultValue, null, options ?? [], null, null, min, max,
            section);

    private static BackendFieldDefinition FixedField(string key, string label, string value, string kind) =>
        new(key, key, label, "fixed", true, false, false, null, null, [], value, kind, null, null, "protocol");
}
