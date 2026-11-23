using System.Text.Json;
using System.Text.Json.Nodes;
using HyPanel.Shared.Contracts;

namespace HyPanel.Agent.Backends.Mihomo;

/// <summary>
/// VLESS + XTLS Vision + REALITY on Mihomo (backend type <c>mihomo-reality</c>). Same config and the same client entry
/// as the Xray backend, so a service switches engines without its users updating their subscriptions.
/// </summary>
internal sealed class MihomoRealityProvider(HttpClient? httpClient, TimeProvider timeProvider)
    : MihomoEngineProvider<MihomoRealityConfig>(httpClient, timeProvider)
{
    public override string BackendType => "mihomo-reality";

    protected override MihomoRealityConfig? Parse(string configJson) =>
        JsonSerializer.Deserialize(configJson, MihomoJsonSerializerContext.Default.MihomoRealityConfig);

    protected override bool IsValidProtocolConfig(MihomoRealityConfig config, ServiceDesiredState desiredState) =>
        config.Flow == RealityValidation.VisionFlow &&
        RealityValidation.IsKey(config.RealityPrivateKey) &&
        RealityValidation.IsKey(config.RealityPublicKey) &&
        RealityValidation.IsShortId(config.ShortId) &&
        IsDnsHostname(config.ServerName) &&
        RealityValidation.IsDestination(config.Destination) &&
        RealityValidation.IsFingerprint(config.Fingerprint);

    protected override JsonObject BuildListener(MihomoRealityConfig config, IReadOnlyList<MihomoIdentity> identities,
        ServiceDesiredState desiredState)
    {
        var list = new JsonArray();
        foreach (var identity in identities)
            list.Add((JsonNode)new JsonObject
            {
                ["username"] = identity.Name, ["uuid"] = identity.Credential, ["flow"] = config.Flow
            });
        return new JsonObject
        {
            ["type"] = "vless",
            ["users"] = list,
            ["reality-config"] = new JsonObject
            {
                ["dest"] = config.Destination,
                ["private-key"] = config.RealityPrivateKey,
                ["short-id"] = new JsonArray(config.ShortId),
                ["server-names"] = new JsonArray(config.ServerName),
                // The handshake target is always dialled directly, as on Xray; otherwise it would follow the rules
                // and a service whose exit is a proxy would borrow the handshake through that proxy (or fail with it).
                ["proxy"] = "DIRECT"
            }
        };
    }
}
