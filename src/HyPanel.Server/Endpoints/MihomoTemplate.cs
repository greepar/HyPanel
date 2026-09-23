namespace HyPanel.Server.Endpoints;

/// <summary>
/// Default Mihomo routing appended to every Mihomo subscription. Rules route through the <c>PROXY</c> group, which
/// lists the user's HyPanel proxies.
/// </summary>
internal static class MihomoTemplate
{
    public const string ProxyGroupName = "PROXY";

    public const string Header = """
        mixed-port: 7890
        allow-lan: false
        mode: rule
        log-level: info

        """;

    public const string RuleProvidersAndRules = """

        rule-providers:
          reject:
            type: http
            behavior: domain
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/reject.txt"
            path: ./ruleset/reject.yaml
            interval: 86400
          icloud:
            type: http
            behavior: domain
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/icloud.txt"
            path: ./ruleset/icloud.yaml
            interval: 86400
          apple:
            type: http
            behavior: domain
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/apple.txt"
            path: ./ruleset/apple.yaml
            interval: 86400
          google:
            type: http
            behavior: domain
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/google.txt"
            path: ./ruleset/google.yaml
            interval: 86400
          proxy:
            type: http
            behavior: domain
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/proxy.txt"
            path: ./ruleset/proxy.yaml
            interval: 86400
          private:
            type: http
            behavior: domain
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/private.txt"
            path: ./ruleset/private.yaml
            interval: 86400
          gfw:
            type: http
            behavior: domain
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/gfw.txt"
            path: ./ruleset/gfw.yaml
            interval: 86400
          telegramcidr:
            type: http
            behavior: ipcidr
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/telegramcidr.txt"
            path: ./ruleset/telegramcidr.yaml
            interval: 86400
          cncidr:
            type: http
            behavior: ipcidr
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/cncidr.txt"
            path: ./ruleset/cncidr.yaml
            interval: 86400
          lancidr:
            type: http
            behavior: ipcidr
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/lancidr.txt"
            path: ./ruleset/lancidr.yaml
            interval: 86400
          applications:
            type: http
            behavior: classical
            url: "https://cdn.jsdelivr.net/gh/Loyalsoldier/clash-rules@release/applications.txt"
            path: ./ruleset/applications.yaml
            interval: 86400

        rules:
          - DOMAIN,clash.razord.top,DIRECT
          - DOMAIN-KEYWORD,qwq.lu,DIRECT
          - DOMAIN-KEYWORD,greepar,DIRECT
          - PROCESS-NAME,rustdesk.exe,DIRECT
          - DOMAIN,yacd.haishan.me,DIRECT
          - RULE-SET,private,DIRECT
          - RULE-SET,reject,REJECT
          - RULE-SET,google,PROXY
          - RULE-SET,proxy,PROXY
          - RULE-SET,gfw,PROXY
          - RULE-SET,icloud,DIRECT
          - RULE-SET,apple,DIRECT
          - RULE-SET,lancidr,DIRECT
          - RULE-SET,cncidr,DIRECT
          - RULE-SET,applications,DIRECT
          - GEOIP,LAN,DIRECT
          - GEOIP,CN,DIRECT
          - MATCH,PROXY

        """;
}
