using System.Text;
using HyPanel.Server.Backends;
using HyPanel.Server.Endpoints;

namespace HyPanel.Server.Subscriptions;

/// <summary>What the admin chose on the rules page: categories turned off and their own rules, placed first.</summary>
internal sealed record SubscriptionRules(IReadOnlyList<string> Disabled, IReadOnlyList<string> CustomRules)
{
    public static SubscriptionRules Default { get; } = new([], []);
}

/// <summary>
/// One kind of traffic with its own policy group in the client, e.g. Telegram or Apple. Turning a category off drops
/// its group and its rules, so that traffic follows the general rules instead.
/// </summary>
/// <param name="Proxies">What the group offers, first entry selected by default.</param>
/// <param name="IncludeNodes">Also list every node in the group, for picking a specific one.</param>
/// <param name="Required">The rules page cannot turn it off; other groups and the final rule rely on it.</param>
/// <param name="DnsRuleSet">A rule set whose domains are resolved through this group (sites that check where DNS comes from).</param>
internal sealed record RuleCategory(string Id, string Name, string Description, IReadOnlyList<string> Proxies,
    bool IncludeNodes = false, bool Required = false, string? DnsRuleSet = null);

/// <summary>
/// The built-in subscription profile: the client settings, the node groups (节点选择, 手动切换 with its three lists,
/// 自动选择), one group per enabled category, the admin's own rules followed by the category rules, and the rule sets
/// those rules use. It is produced as a template, so the same renderer fills in the nodes and an admin can start a
/// fully custom template from it.
/// </summary>
internal static class BuiltinProfile
{
    public const string NodeSelect = "🚀 节点选择";
    public const string Manual = "✈️ 手动切换";
    public const string Auto = "♻️ 自动选择";
    public const string DirectNodes = "🔗 直连节点";
    public const string DirectV6Nodes = "🌐 直连节点 v6";
    public const string RelayNodes = "🔀 代理节点";
    public const string GlobalDirect = "🎯 全球直连";
    public const string Final = "🐟 漏网之鱼";
    public const int MaximumCustomRules = 500;
    public const int MaximumRuleLength = 512;

    private static readonly string[] Usual = [Manual, NodeSelect, Auto, "DIRECT"];
    private static readonly string[] DirectFirst = [Manual, "DIRECT", NodeSelect];

    public static IReadOnlyList<RuleCategory> Categories { get; } =
    [
        new("direct", GlobalDirect, "局域网、国内网站和下载直连", ["DIRECT", NodeSelect, Auto], Required: true),
        new("final", Final, "没有命中任何规则的流量", Usual, Required: true),
        new("ads", "🚫 广告拦截", "拦截常见广告域名", ["REJECT", "DIRECT"]),
        new("app-ads", "🍃 应用净化", "拦截应用内广告和统计", ["REJECT", "DIRECT"]),
        new("telegram", "✈️ Telegram", "Telegram", Usual),
        new("openai", "🤖 ChatGPT", "ChatGPT / OpenAI，域名经所选节点解析", [Manual, NodeSelect], IncludeNodes: true,
            DnsRuleSet: "OpenAI"),
        new("anthropic", "🧠 Claude", "Claude / Anthropic，域名经所选节点解析", [Manual, NodeSelect], IncludeNodes: true,
            DnsRuleSet: "Anthropic"),
        new("google", "🔎 谷歌服务", "Google 搜索、Gmail 等谷歌服务", [NodeSelect, Manual, Auto, "DIRECT"], IncludeNodes: true),
        new("fcm", "📢 谷歌FCM", "安卓推送（Firebase Cloud Messaging）", [NodeSelect, Manual, "DIRECT"], IncludeNodes: true),
        new("youtube", "📹 Youtube", "YouTube", Usual),
        new("netflix", "🎥 奈飞视频", "Netflix", Usual),
        new("bahamut", "📺 巴哈姆特", "巴哈姆特动画疯", [Manual, NodeSelect, "DIRECT"]),
        new("bilibili", "📺 哔哩哔哩", "哔哩哔哩（含港澳台番剧）", [GlobalDirect, NodeSelect]),
        new("proxy-media", "🌍 国外媒体", "其他海外视频和音乐平台", Usual),
        new("china-media", "🌏 国内媒体", "国内视频和音乐平台", ["DIRECT", Manual]),
        new("netease", "🎶 网易音乐", "网易云音乐", ["DIRECT", Manual, NodeSelect, Auto]),
        new("bing", "🔎 微软Bing", "Bing", DirectFirst),
        new("onedrive", "☁️ 微软云盘", "OneDrive", DirectFirst),
        new("microsoft", "Ⓜ️ 微软服务", "其他微软服务", DirectFirst),
        new("apple", "🍎 苹果服务", "苹果服务", DirectFirst),
        new("docker", "🐳 Docker", "Docker Hub 等", Usual),
        new("games", "🎮 游戏平台", "Steam、Epic、Origin、PlayStation、Nintendo", [Manual, "DIRECT"], IncludeNodes: true),
        new("roblox", "🎮 Roblox", "Roblox，域名经所选节点解析", [Manual, "DIRECT"], IncludeNodes: true,
            DnsRuleSet: "Roblox"),
    ];

    /// <summary>The rules in order, each with the category whose group it sends traffic to.</summary>
    private static readonly (string Rule, string Category)[] Rules =
    [
        ("RULE-SET,LocalAreaNetwork", "direct"),
        ("RULE-SET,UnBan", "direct"),
        ("RULE-SET,AdsMeta", "ads"),
        ("RULE-SET,AdvertisingLite", "ads"),
        ("RULE-SET,BanAD", "ads"),
        ("RULE-SET,BanProgramAD", "app-ads"),
        ("RULE-SET,GoogleFCM", "fcm"),
        ("RULE-SET,GoogleAdDomains", "google"),
        ("RULE-SET,GoogleDomain", "google"),
        ("RULE-SET,GoogleIP", "google"),
        ("RULE-SET,SteamCN", "direct"),
        ("RULE-SET,Bing", "bing"),
        ("RULE-SET,OneDrive", "onedrive"),
        ("RULE-SET,Microsoft", "microsoft"),
        ("RULE-SET,Apple", "apple"),
        ("RULE-SET,Telegram", "telegram"),
        ("RULE-SET,OpenAI", "openai"),
        ("RULE-SET,Anthropic", "anthropic"),
        ("RULE-SET,Docker", "docker"),
        ("RULE-SET,NetEaseMusic", "netease"),
        ("RULE-SET,Roblox", "roblox"),
        ("RULE-SET,RobloxIP", "roblox"),
        ("RULE-SET,Epic", "games"),
        ("RULE-SET,Origin", "games"),
        ("RULE-SET,Sony", "games"),
        ("RULE-SET,Steam", "games"),
        ("RULE-SET,Nintendo", "games"),
        ("RULE-SET,YouTube", "youtube"),
        ("RULE-SET,Netflix", "netflix"),
        ("RULE-SET,Bahamut", "bahamut"),
        ("RULE-SET,BilibiliHMT", "bilibili"),
        ("RULE-SET,Bilibili", "bilibili"),
        ("RULE-SET,ChinaMedia", "china-media"),
        ("RULE-SET,ProxyMedia", "proxy-media"),
        ("RULE-SET,ProxyGFWlist", "proxy"),
        ("RULE-SET,ChinaDomain", "direct"),
        ("RULE-SET,ChinaCompanyIp", "direct"),
        ("RULE-SET,Download", "direct"),
        ("GEOIP,CN", "direct"),
    ];

    /// <summary>Rule options that belong after the policy.</summary>
    private static readonly Dictionary<string, string> RuleOptions = new(StringComparer.Ordinal)
    {
        ["GEOIP,CN"] = ",no-resolve",
    };

    private static readonly string Base = Load("HyPanel.Server.BuiltinProfileBase.yaml");
    private static readonly IReadOnlyDictionary<string, string> Providers = ParseProviders(Load("HyPanel.Server.BuiltinRuleProviders.yaml"));

    /// <summary>The template with every category on and no custom rules.</summary>
    public static string DefaultTemplate { get; } = BuildTemplate(SubscriptionRules.Default);

    /// <summary>Names a custom rule may send traffic to: built-in policies and every group the profile has.</summary>
    public static IReadOnlyList<string> Policies(SubscriptionRules rules) =>
    [
        "DIRECT", "REJECT", "REJECT-DROP", NodeSelect, Manual, Auto, DirectNodes, DirectV6Nodes, RelayNodes,
        .. Enabled(rules).Select(static category => category.Name)
    ];

    public static string BuildTemplate(SubscriptionRules rules)
    {
        var enabled = Enabled(rules).ToArray();
        var ids = enabled.Select(static category => category.Id).Append("proxy").ToHashSet(StringComparer.Ordinal);
        var output = new StringBuilder(Base.Length + 16 * 1024);

        // Domains of some categories are resolved through the category's own group, so the site sees DNS from there.
        var nameservers = new StringBuilder();
        foreach (var category in enabled.Where(static category => category.DnsRuleSet is not null))
        {
            nameservers.Append("    rule-set:").Append(category.DnsRuleSet).Append(":\n");
            foreach (var server in new[] { "https://1.1.1.1/dns-query", "https://dns.quad9.net/dns-query" })
                nameservers.Append("    - ").Append(server).Append('#').Append(category.Name).Append('\n');
        }
        output.Append(Base.Replace("    __RULE_SET_NAMESERVERS__\n", nameservers.ToString(), StringComparison.Ordinal));

        output.Append("proxy-groups:\n");
        Group(output, NodeSelect, "select", [Manual, Auto, "DIRECT"]);
        Group(output, Manual, "select", [DirectNodes, DirectV6Nodes, RelayNodes]);
        Group(output, DirectNodes, "select", [MihomoTemplate.DirectPlaceholder]);
        Group(output, DirectV6Nodes, "select", [MihomoTemplate.DirectV6Placeholder]);
        Group(output, RelayNodes, "select", [MihomoTemplate.RelayPlaceholder]);
        output.Append("  - name: ").Append(Quote(Auto)).Append("\n    type: url-test\n")
            .Append("    url: https://www.gstatic.com/generate_204\n    interval: 600\n    tolerance: 50\n    lazy: true\n")
            .Append("    timeout: 5000\n    expected-status: 204\n    proxies:\n      - ")
            .Append(MihomoTemplate.AllProxiesPlaceholder).Append('\n');
        foreach (var category in enabled)
            Group(output, category.Name, "select",
                [.. category.Proxies.Where(proxy => proxy is "DIRECT" or "REJECT" || !IsCategory(proxy) || IsEnabledGroup(proxy, enabled)),
                 .. category.IncludeNodes ? [MihomoTemplate.AllProxiesPlaceholder] : Array.Empty<string>()]);

        output.Append("\nrules:\n");
        foreach (var rule in rules.CustomRules) output.Append("  - ").Append(Quote(rule)).Append('\n');
        var used = new List<string>();
        foreach (var (rule, categoryId) in Rules)
        {
            if (!ids.Contains(categoryId)) continue;
            var target = categoryId == "proxy" ? NodeSelect : enabled.First(category => category.Id == categoryId).Name;
            output.Append("  - ").Append(Quote($"{rule},{target}{RuleOptions.GetValueOrDefault(rule)}")).Append('\n');
            if (rule.StartsWith("RULE-SET,", StringComparison.Ordinal)) used.Add(rule["RULE-SET,".Length..]);
        }
        output.Append("  - ").Append(Quote($"MATCH,{Final}")).Append('\n');

        output.Append("\nrule-providers:\n");
        foreach (var name in used.Distinct()) output.Append(Providers[name]);
        return output.ToString();
    }

    /// <summary>Checks a custom rule: TYPE,VALUE,POLICY[,options] (or MATCH,POLICY) with a known policy.</summary>
    public static bool TryValidateRule(string rule, IReadOnlyCollection<string> policies, out string error)
    {
        error = string.Empty;
        var parts = rule.Split(',');
        if (rule.Length > MaximumRuleLength || rule.Any(char.IsControl))
            error = $"规则“{Shorten(rule)}”太长或包含控制字符。";
        else if (parts[0] == "MATCH")
            error = "不需要写 MATCH，没有命中的流量会交给“🐟 漏网之鱼”。";
        else if (parts.Length < 3 || parts.Take(3).Any(string.IsNullOrWhiteSpace))
            error = $"规则“{Shorten(rule)}”格式不对，应为“类型,内容,策略”，例如 DOMAIN-SUFFIX,example.com,DIRECT。";
        else if (!policies.Contains(parts[2].Trim()))
            error = $"规则“{Shorten(rule)}”的策略“{parts[2].Trim()}”不存在，可用：{string.Join("、", policies)}。";
        return error.Length == 0;
    }

    private static string Shorten(string rule) => rule.Length > 40 ? rule[..40] + "…" : rule;

    private static IEnumerable<RuleCategory> Enabled(SubscriptionRules rules) =>
        Categories.Where(category => category.Required || !rules.Disabled.Contains(category.Id));

    private static bool IsCategory(string name) => Categories.Any(category => category.Name == name);

    private static bool IsEnabledGroup(string name, IEnumerable<RuleCategory> enabled) =>
        enabled.Any(category => category.Name == name);

    private static void Group(StringBuilder output, string name, string type, IEnumerable<string> proxies)
    {
        output.Append("  - name: ").Append(Quote(name)).Append("\n    type: ").Append(type).Append("\n    proxies:\n");
        foreach (var proxy in proxies)
            output.Append("      - ").Append(proxy.StartsWith("__", StringComparison.Ordinal) ? proxy : Quote(proxy)).Append('\n');
    }

    private static string Quote(string value) => SubscriptionProxy.Quote(value);

    private static string Load(string name)
    {
        using var stream = typeof(BuiltinProfile).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"{name} is not embedded.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Splits the rule-provider list into one block per provider, keyed by name.</summary>
    private static Dictionary<string, string> ParseProviders(string text)
    {
        var providers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var block = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("  ", StringComparison.Ordinal) && !line.StartsWith("   ", StringComparison.Ordinal)
                && line.EndsWith(':'))
            {
                if (current is not null) providers[current] = block.ToString();
                current = line.Trim().TrimEnd(':');
                block.Clear();
            }
            if (current is not null && line.Length > 0) block.Append(line).Append('\n');
        }
        if (current is not null) providers[current] = block.ToString();
        return providers;
    }
}
