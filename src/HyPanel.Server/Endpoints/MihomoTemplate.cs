using System.Text;
using HyPanel.Server.Backends;

namespace HyPanel.Server.Endpoints;

/// <summary>
/// Mihomo subscription template. A template is ordinary Mihomo YAML with placeholders: the top-level
/// <c>proxies:</c> line is replaced by the user's proxies, and a list item <c>- __ALL_PROXIES__</c> expands to all
/// proxy names; <c>- __DIRECT_PROXIES__</c>, <c>- __DIRECT_V6_PROXIES__</c> and <c>- __RELAY_PROXIES__</c> expand to
/// the direct lines over IPv4, the direct lines over IPv6 and the lines through a proxy. Groups may also use Mihomo's
/// own <c>include-all</c> + <c>filter</c> to select proxies by name.
/// </summary>
internal static class MihomoTemplate
{
    public const string AllProxiesPlaceholder = "__ALL_PROXIES__";

    /// <summary>The list placeholders and which entries each takes; null takes them all.</summary>
    private static readonly Dictionary<string, SubscriptionGroup?> Placeholders = new(StringComparer.Ordinal)
    {
        [AllProxiesPlaceholder] = null,
        ["__DIRECT_PROXIES__"] = SubscriptionGroup.Direct,
        ["__DIRECT_V6_PROXIES__"] = SubscriptionGroup.DirectIpv6,
        ["__RELAY_PROXIES__"] = SubscriptionGroup.Relay,
    };
    public const int MaximumLength = 512 * 1024;

    public static string Default { get; } = LoadDefault();

    private static string LoadDefault()
    {
        using var stream = typeof(MihomoTemplate).Assembly.GetManifestResourceStream("HyPanel.Server.DefaultMihomoTemplate.yaml")
                           ?? throw new InvalidOperationException("The default Mihomo template is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Basic structural checks; Mihomo itself validates the rest when the client loads it.</summary>
    public static bool TryValidate(string? template, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(template)) error = "模板不能为空。";
        else if (template.Length > MaximumLength) error = "模板过大（上限 512 KB）。";
        else if (template.Contains('\t')) error = "YAML 不能使用 Tab 缩进。";
        else if (!HasTopLevelKey(template, "proxy-groups")) error = "模板缺少 proxy-groups。";
        else if (!HasTopLevelKey(template, "rules")) error = "模板缺少 rules。";
        return error.Length == 0;
    }

    public static string Render(string template, IReadOnlyList<(string Name, string Yaml, SubscriptionGroup Group)> proxies,
        Func<string, string> quote)
    {
        var output = new StringBuilder(template.Length + proxies.Count * 256);
        var hasProxiesKey = false;
        foreach (var rawLine in template.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine;
            if (IsTopLevelKey(line, "proxies"))
            {
                hasProxiesKey = true;
                AppendProxies(output, proxies);
                continue;
            }
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) &&
                Placeholders.TryGetValue(trimmed[2..].Trim().Trim('"'), out var group))
            {
                var indent = line[..(line.Length - trimmed.Length)];
                var names = proxies.Where(proxy => group is null || proxy.Group == group).Select(proxy => proxy.Name).ToArray();
                // A group must list something; an empty one would make Mihomo reject the whole profile.
                if (names.Length == 0) output.Append(indent).Append("- DIRECT\n");
                foreach (var name in names) output.Append(indent).Append("- ").Append(quote(name)).Append('\n');
                continue;
            }
            output.Append(line).Append('\n');
        }
        if (!hasProxiesKey)
        {
            var block = new StringBuilder();
            AppendProxies(block, proxies);
            output.Insert(0, block.ToString());
        }
        return output.ToString().TrimEnd('\n') + "\n";
    }

    private static void AppendProxies(StringBuilder output,
        IReadOnlyList<(string Name, string Yaml, SubscriptionGroup Group)> proxies)
    {
        if (proxies.Count == 0)
        {
            output.Append("proxies: []\n");
            return;
        }
        output.Append("proxies:\n");
        foreach (var proxy in proxies) output.Append(proxy.Yaml);
    }

    private static bool HasTopLevelKey(string template, string key) =>
        template.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Any(line => IsTopLevelKey(line, key));

    private static bool IsTopLevelKey(string line, string key) =>
        line.StartsWith(key + ":", StringComparison.Ordinal) &&
        (line.Length == key.Length + 1 || line[key.Length + 1] is ' ' or '#');
}
