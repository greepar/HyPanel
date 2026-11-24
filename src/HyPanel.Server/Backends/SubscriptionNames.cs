using HyPanel.Server.Persistence;

namespace HyPanel.Server.Backends;

/// <summary>Which list a subscription entry belongs to; the template places each list with its own placeholder.</summary>
internal enum SubscriptionGroup
{
    /// <summary>Goes out directly from the node, dialled over IPv4 (or a custom address).</summary>
    Direct,

    /// <summary>Goes out directly from the node, dialled over the node's IPv6.</summary>
    DirectIpv6,

    /// <summary>Leaves through an upstream proxy, over either address.</summary>
    Relay
}

/// <summary>
/// How subscription entries are named. Every name starts with the node's flag and city, then the node's name (the
/// provider, as the admin names it):
/// <list type="bullet">
/// <item>direct: <c>🇯🇵 东京 · 搬瓦工 Hy2</c>, over IPv6 <c>🇭🇰 香港 · Bage VLESS [v6]</c>;</item>
/// <item>through a proxy: <c>🇺🇸 洛杉矶 · DMIT -> 🇫🇷 WS130</c>, over IPv6 <c>🇯🇵 东京 · YUNYOO [v6] -> 🇫🇷 WS130</c>:
/// after the arrow the flag of the country the proxy leaves from (once the Agent has found it) and the line's name.</item>
/// </list>
/// </summary>
internal static class SubscriptionNames
{
    public const string Ipv6Mark = "[v6]";

    /// <summary>A direct line. An extra direct line (a route) adds its name, so it differs from the default line.</summary>
    public static string Direct(string? countryCode, string? city, string node, string protocol, bool ipv6,
        string? route = null) =>
        Join(Place(countryCode, city), $"{node} {protocol}{(ipv6 ? " " + Ipv6Mark : "")}{(route is null ? "" : " · " + route)}");

    /// <summary>
    /// A line that leaves through a proxy; after the arrow come the proxy's exit country (<paramref name="exitCountry"/>)
    /// and <paramref name="line"/>, its name.
    /// </summary>
    public static string Relay(string? countryCode, string? city, string node, bool ipv6, string line,
        string? exitCountry) =>
        Join(Place(countryCode, city), $"{node}{(ipv6 ? " " + Ipv6Mark : "")} -> {Join(Place(exitCountry, null), line, " ")}");

    /// <summary>The flag and city, whichever are known.</summary>
    private static string Place(string? countryCode, string? city)
    {
        var flag = countryCode is null ? string.Empty : SqliteServerRepository.CountryFlag(countryCode);
        return $"{flag} {city}".Trim();
    }

    /// <summary>
    /// The city to show for a node: the admin's own, else the looked-up city when its Chinese name is known, else the
    /// region's (the lookup sometimes names a district, e.g. "Asagaya-minami" in Tokyo), else whatever was found.
    /// </summary>
    public static string? DisplayCity(string? custom, string? city, string? region)
    {
        if (!string.IsNullOrWhiteSpace(custom)) return custom.Trim();
        if (city is not null && Cities.TryGetValue(city.Trim(), out var known)) return known;
        if (region is not null && Cities.TryGetValue(region.Trim(), out var regional)) return regional;
        return string.IsNullOrWhiteSpace(city) ? string.IsNullOrWhiteSpace(region) ? null : region.Trim() : city.Trim();
    }

    private static string Join(string place, string rest, string separator = " · ") =>
        place.Length == 0 ? rest : place + separator + rest;


    private static readonly Dictionary<string, string> Cities = new(StringComparer.OrdinalIgnoreCase)
    {
        // East Asia
        ["Hong Kong"] = "香港", ["Central"] = "香港", ["Kowloon"] = "香港", ["Tsuen Wan"] = "香港", ["Kwai Chung"] = "香港",
        ["Macau"] = "澳门", ["Macao"] = "澳门",
        ["Taipei"] = "台北", ["New Taipei"] = "新北", ["Taichung"] = "台中", ["Kaohsiung"] = "高雄", ["Changhua"] = "彰化",
        ["Tokyo"] = "东京", ["Osaka"] = "大阪", ["Kawasaki"] = "川崎", ["Yokohama"] = "横滨", ["Saitama"] = "埼玉",
        ["Chiba"] = "千叶", ["Nagoya"] = "名古屋", ["Fukuoka"] = "福冈", ["Sapporo"] = "札幌", ["Kyoto"] = "京都",
        ["Osaka Prefecture"] = "大阪", ["Kanagawa"] = "神奈川", ["Aichi"] = "名古屋", ["Hokkaido"] = "北海道",
        ["Seoul"] = "首尔", ["Chuncheon"] = "春川", ["Busan"] = "釜山", ["Incheon"] = "仁川",
        ["Beijing"] = "北京", ["Shanghai"] = "上海", ["Guangzhou"] = "广州", ["Shenzhen"] = "深圳",
        ["Hangzhou"] = "杭州", ["Chengdu"] = "成都", ["Qingdao"] = "青岛", ["Zhangjiakou"] = "张家口",
        ["Hohhot"] = "呼和浩特", ["Guangdong"] = "广东", ["Zhejiang"] = "浙江", ["Jiangsu"] = "江苏",
        ["Inner Mongolia"] = "内蒙古", ["Sichuan"] = "四川", ["Hebei"] = "河北", ["Shandong"] = "山东",
        ["Fujian"] = "福建", ["Hubei"] = "湖北", ["Taiwan"] = "台湾", ["Ningbo"] = "宁波", ["Nanjing"] = "南京", ["Wuhan"] = "武汉", ["Chongqing"] = "重庆",
        // Southeast and South Asia
        ["Singapore"] = "新加坡", ["Kuala Lumpur"] = "吉隆坡", ["Bangkok"] = "曼谷", ["Jakarta"] = "雅加达",
        ["Manila"] = "马尼拉", ["Ho Chi Minh City"] = "胡志明市", ["Hanoi"] = "河内", ["Mumbai"] = "孟买",
        ["New Delhi"] = "新德里", ["Delhi"] = "德里", ["Bangalore"] = "班加罗尔", ["Chennai"] = "金奈",
        // North America
        ["Los Angeles"] = "洛杉矶", ["San Jose"] = "圣何塞", ["Santa Clara"] = "圣克拉拉", ["Fremont"] = "弗里蒙特",
        ["San Francisco"] = "旧金山", ["Palo Alto"] = "帕洛阿尔托", ["Seattle"] = "西雅图", ["Portland"] = "波特兰",
        ["Las Vegas"] = "拉斯维加斯", ["Phoenix"] = "凤凰城", ["Salt Lake City"] = "盐湖城", ["Denver"] = "丹佛",
        ["Dallas"] = "达拉斯", ["Houston"] = "休斯顿", ["Kansas City"] = "堪萨斯城", ["Chicago"] = "芝加哥",
        ["Atlanta"] = "亚特兰大", ["Miami"] = "迈阿密", ["Ashburn"] = "阿什本", ["New York"] = "纽约",
        ["Newark"] = "纽瓦克", ["Piscataway"] = "皮斯卡特维", ["Secaucus"] = "锡考克斯", ["Buffalo"] = "布法罗",
        ["Washington"] = "华盛顿", ["Boston"] = "波士顿", ["Toronto"] = "多伦多", ["Montreal"] = "蒙特利尔",
        ["Vancouver"] = "温哥华", ["Mexico City"] = "墨西哥城", ["California"] = "加州", ["Virginia"] = "弗吉尼亚",
        ["Texas"] = "德州", ["Oregon"] = "俄勒冈", ["New Jersey"] = "新泽西", ["Illinois"] = "伊利诺伊",
        ["Florida"] = "佛州", ["Georgia"] = "佐治亚", ["Arizona"] = "亚利桑那", ["Nevada"] = "内华达", ["Utah"] = "犹他",
        // Europe
        ["London"] = "伦敦", ["England"] = "英格兰", ["Hesse"] = "黑森", ["North Holland"] = "北荷兰", ["Île-de-France"] = "巴黎", ["Manchester"] = "曼彻斯特", ["Frankfurt am Main"] = "法兰克福", ["Frankfurt"] = "法兰克福",
        ["Falkenstein"] = "法尔肯施泰因", ["Nuremberg"] = "纽伦堡", ["Berlin"] = "柏林", ["Munich"] = "慕尼黑",
        ["Düsseldorf"] = "杜塞尔多夫", ["Amsterdam"] = "阿姆斯特丹", ["Rotterdam"] = "鹿特丹", ["Paris"] = "巴黎",
        ["Roubaix"] = "鲁贝", ["Gravelines"] = "格拉沃利讷", ["Marseille"] = "马赛", ["Brussels"] = "布鲁塞尔",
        ["Zurich"] = "苏黎世", ["Vienna"] = "维也纳", ["Milan"] = "米兰", ["Madrid"] = "马德里", ["Lisbon"] = "里斯本",
        ["Stockholm"] = "斯德哥尔摩", ["Helsinki"] = "赫尔辛基", ["Oslo"] = "奥斯陆", ["Copenhagen"] = "哥本哈根",
        ["Warsaw"] = "华沙", ["Prague"] = "布拉格", ["Bucharest"] = "布加勒斯特", ["Sofia"] = "索非亚",
        ["Dublin"] = "都柏林", ["Moscow"] = "莫斯科", ["Saint Petersburg"] = "圣彼得堡", ["Istanbul"] = "伊斯坦布尔",
        ["Kyiv"] = "基辅",
        // Elsewhere
        ["Sydney"] = "悉尼", ["Melbourne"] = "墨尔本", ["Auckland"] = "奥克兰", ["Dubai"] = "迪拜",
        ["Tel Aviv"] = "特拉维夫", ["Johannesburg"] = "约翰内斯堡", ["São Paulo"] = "圣保罗", ["Sao Paulo"] = "圣保罗",
    };
}
