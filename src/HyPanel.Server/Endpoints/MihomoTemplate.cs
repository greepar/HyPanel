namespace HyPanel.Server.Endpoints;

using System.Text;

/// <summary>
/// Mihomo subscription template. A template is ordinary Mihomo YAML with two placeholders:
/// the top-level <c>proxies:</c> line is replaced by the user's proxies, and every list item
/// <c>- __ALL_PROXIES__</c> expands to all proxy names. Groups may also use Mihomo's own
/// <c>include-all</c> + <c>filter</c> to select proxies by name.
/// </summary>
internal static class MihomoTemplate
{
    public const string AllProxiesPlaceholder = "__ALL_PROXIES__";
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

    public static string Render(string template, IReadOnlyList<(string Name, string Yaml)> proxies,
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
                trimmed[2..].Trim() is var item && (item == AllProxiesPlaceholder || item == $"\"{AllProxiesPlaceholder}\""))
            {
                var indent = line[..(line.Length - trimmed.Length)];
                if (proxies.Count == 0) output.Append(indent).Append("- DIRECT\n");
                foreach (var proxy in proxies) output.Append(indent).Append("- ").Append(quote(proxy.Name)).Append('\n');
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

    private static void AppendProxies(StringBuilder output, IReadOnlyList<(string Name, string Yaml)> proxies)
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
