using System.Text.Json;
using System.Text.Json.Nodes;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Networking;

namespace HyPanel.Agent.Backends.Mihomo;

/// <summary>
/// Shadowsocks 2022 on Mihomo (backend type <c>mihomo-ss2022</c>), TCP and UDP on one port. Every user has a key of
/// their own derived from their credential; Mihomo accepts sing-mux from clients without extra settings.
/// </summary>
internal sealed class MihomoShadowsocksProvider(HttpClient? httpClient, TimeProvider timeProvider)
    : MihomoEngineProvider<MihomoShadowsocksConfig>(httpClient, timeProvider)
{
    public override string BackendType => "mihomo-ss2022";

    protected override bool AlsoListensOnUdp => true;

    protected override MihomoShadowsocksConfig? Parse(string configJson) =>
        JsonSerializer.Deserialize(configJson, MihomoJsonSerializerContext.Default.MihomoShadowsocksConfig);

    protected override bool IsValidProtocolConfig(MihomoShadowsocksConfig config, ServiceDesiredState desiredState) =>
        config.Cipher == Shadowsocks2022.Cipher && Shadowsocks2022.IsKey(config.ServerKey);

    protected override JsonObject BuildListener(MihomoShadowsocksConfig config, IReadOnlyList<MihomoIdentity> identities,
        ServiceDesiredState desiredState)
    {
        var map = new JsonObject();
        foreach (var identity in identities) map[identity.Name] = Shadowsocks2022.UserKey(identity.Credential);
        var listener = new JsonObject
        {
            ["type"] = "shadowsocks",
            ["cipher"] = config.Cipher,
            ["password"] = config.ServerKey,
            ["udp"] = true
        };
        // Without users Mihomo would accept the server key alone; with an empty map nobody gets in.
        listener["users"] = map;
        return listener;
    }
}
