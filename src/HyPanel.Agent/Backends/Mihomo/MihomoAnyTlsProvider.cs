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

    /// <summary>A certificate kept on the node outside the instance directory (Path kind) must be readable.</summary>
    protected override IEnumerable<string> SafePaths(ServiceDesiredState desiredState) =>
        desiredState.TlsCertificate is { Kind: TlsCertificateKinds.Path } tls
            ? [Path.GetDirectoryName(tls.CertificatePath!)!, Path.GetDirectoryName(tls.PrivateKeyPath!)!]
            : [];

    protected override JsonObject BuildListener(MihomoAnyTlsConfig config, IReadOnlyList<MihomoIdentity> identities,
        ServiceDesiredState desiredState)
    {
        var tls = desiredState.TlsCertificate!;
        var directory = Path.Combine(instanceStore.GetInstanceDirectory(desiredState.ServiceId), "tls", tls.Fingerprint);
        var (certificate, key) = tls.Kind == TlsCertificateKinds.Path
            ? (tls.CertificatePath!, tls.PrivateKeyPath!)
            : (Path.Combine(directory, "cert.pem"), Path.Combine(directory, "key.pem"));
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
