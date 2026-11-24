using System.Text.Json;
using System.Text.Json.Nodes;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Shared.Contracts;

namespace HyPanel.Agent.Backends.Mihomo;

/// <summary>
/// AnyTLS on Mihomo (backend type <c>mihomo-anytls</c>): TLS with a certificate from the Panel, each user logging in
/// with their own password. Mihomo cannot issue certificates itself, so the certificate is uploaded or on disk.
/// </summary>
internal sealed class MihomoAnyTlsProvider(HttpClient? httpClient, TimeProvider timeProvider,
    BackendInstanceStore instanceStore) : MihomoEngineProvider<MihomoAnyTlsConfig>(httpClient, timeProvider)
{
    public override string BackendType => "mihomo-anytls";

    protected override MihomoAnyTlsConfig? Parse(string configJson) =>
        JsonSerializer.Deserialize(configJson, MihomoJsonSerializerContext.Default.MihomoAnyTlsConfig);

    protected override bool IsValidProtocolConfig(MihomoAnyTlsConfig config, ServiceDesiredState desiredState) =>
        config.CertificateId != Guid.Empty &&
        desiredState.TlsCertificate is { Kind: TlsCertificateKinds.Upload or TlsCertificateKinds.Path } tls &&
        tls.CertificateId == config.CertificateId;

    /// <summary>The certificate lives outside the instance directory (shared copy, or the admin's own files).</summary>
    protected override IEnumerable<string> SafePaths(ServiceDesiredState desiredState)
    {
        if (desiredState.TlsCertificate is not { } tls) return [];
        var (certificate, key) = instanceStore.CertificateFiles(tls);
        return [Path.GetDirectoryName(certificate)!, Path.GetDirectoryName(key)!];
    }

    protected override JsonObject BuildListener(MihomoAnyTlsConfig config, IReadOnlyList<MihomoIdentity> identities,
        ServiceDesiredState desiredState)
    {
        var tls = desiredState.TlsCertificate!;
        var (certificate, key) = instanceStore.CertificateFiles(tls);
        var map = new JsonObject();
        foreach (var identity in identities) map[identity.Name] = identity.Credential;
        return new JsonObject
        {
            ["type"] = "anytls",
            ["users"] = map,
            ["certificate"] = certificate,
            ["private-key"] = key
        };
    }
}
