using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HyPanel.Shared.Networking;

/// <summary>
/// An extra line of a REALITY service: same port, camouflage target and keys, but its own name in subscriptions and its
/// own way out (direct or an upstream proxy). Users reach a route with a UUID derived from their service credential,
/// and the Xray router sends that UUID's traffic to the route's outbound. Stored in the service config as
/// <c>routes: [{ id, name, upstreamProxy… }]</c>; the service's own upstream fields are the default line.
/// </summary>
public sealed record ServiceRoute(Guid Id, string Name, UpstreamProxy? Proxy)
{
    public const int MaximumRoutes = 16;
    public const int MaximumNameLength = 32;

    /// <summary>
    /// The UUID a user dials to take <paramref name="routeId"/>: stable for the credential, unguessable without it, and
    /// different for every route, so the router can tell lines apart. Both the Panel (subscriptions) and the Agent
    /// (Xray clients) compute it, so the desired state does not need to carry it.
    /// </summary>
    public static string Credential(string baseCredential, Guid routeId)
    {
        Span<byte> input = stackalloc byte[16 + Encoding.UTF8.GetMaxByteCount(baseCredential.Length)];
        routeId.TryWriteBytes(input, bigEndian: true, out _);
        var length = 16 + Encoding.UTF8.GetBytes(baseCredential, input[16..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(input[..length], hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x80); // version 8: application-defined
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true).ToString("D");
    }

    /// <summary>The Xray client email (stats and routing key) of a user on the default line or on a route.</summary>
    public static string UserTag(Guid userId, Guid? routeId = null) =>
        routeId is { } id ? $"hypanel-{userId:N}-{id:N}" : $"hypanel-{userId:N}";

    /// <summary>
    /// Reads and validates the <c>routes</c> array of a service config. Returns true with an empty list when there is
    /// none; false with a message the admin can act on when it is malformed.
    /// </summary>
    public static bool TryParse(JsonElement config, out IReadOnlyList<ServiceRoute> routes, out string? error)
    {
        routes = [];
        error = null;
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("routes", out var array)
            || array.ValueKind == JsonValueKind.Null) return true;
        if (array.ValueKind != JsonValueKind.Array) return Fail("线路配置格式无效。", out error);
        if (array.GetArrayLength() > MaximumRoutes) return Fail($"一个服务最多 {MaximumRoutes} 条附加线路。", out error);
        var list = new List<ServiceRoute>();
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return Fail("线路配置格式无效。", out error);
            string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (!Guid.TryParse(Text("id"), out var id) || id == Guid.Empty || !ids.Add(id))
                return Fail("线路缺少有效且唯一的 ID。", out error);
            var name = Text("name")?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > MaximumNameLength || name.Any(char.IsControl))
                return Fail($"线路名称不能为空，且不超过 {MaximumNameLength} 个字符。", out error);
            if (!names.Add(name)) return Fail($"线路名称“{name}”重复了。", out error);
            int? port = null;
            if (item.TryGetProperty("upstreamProxyPort", out var portValue) && portValue.ValueKind != JsonValueKind.Null)
            {
                if (portValue.ValueKind != JsonValueKind.Number || !portValue.TryGetInt32(out var number))
                    return Fail($"线路“{name}”的代理端口必须是 1 到 65535 之间的整数。", out error);
                port = number;
            }
            if (!UpstreamProxy.TryCreate(Text("upstreamProxyType"), Text("upstreamProxyHost"), port,
                    Text("upstreamProxyUsername"), Text("upstreamProxyPassword"), out var proxy, out var proxyError))
                return Fail($"线路“{name}”：{proxyError}", out error);
            list.Add(new ServiceRoute(id, name, proxy));
        }
        routes = list;
        return true;
    }

    /// <summary>The routes of a config string; empty when there are none or they are invalid.</summary>
    public static IReadOnlyList<ServiceRoute> Parse(string configJson)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return TryParse(document.RootElement, out var routes, out _) ? routes : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }
}
