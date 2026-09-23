using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyPanel.Server.Endpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Endpoints;

[TestClass]
public sealed class AdminCertificatesEndpointsTests
{
    [TestMethod]
    public void TryCreate_MalformedPem_IsRejected()
    {
        Assert.IsFalse(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(),
            new CertificateUploadRequest("bad", "not-a-certificate", "not-a-key"), DateTimeOffset.UtcNow, out _));
    }

    [TestMethod]
    public void TryCreate_MatchingValidCertificateAndKey_ReturnsMetadataWithoutResponseKey()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=example.com", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("example.com");
        san.AddDnsName("www.example.com");
        request.CertificateExtensions.Add(san.Build());
        using var cert = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(30));
        var body = new CertificateUploadRequest("example", cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem());

        Assert.IsTrue(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(), body, now, out var value));

        Assert.AreEqual("example", value.Name);
        Assert.IsNotNull(value.PrivateKeyPem);
        Assert.AreEqual(64, value.Fingerprint!.Length);
        StringAssert.Contains(value.San, "example.com");
    }

    [TestMethod]
    public void TryCreate_MismatchedKey_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        using var other = RSA.Create(2048);
        var request = new CertificateRequest("CN=example.com", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(now.AddMinutes(-1), now.AddDays(30));

        Assert.IsFalse(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(),
            new CertificateUploadRequest("bad", cert.ExportCertificatePem(), other.ExportPkcs8PrivateKeyPem()), now,
            out _));
    }

    [TestMethod]
    public void TryCreate_ExpiredCertificate_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=expired.example", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(now.AddDays(-2), now.AddDays(-1));

        Assert.IsFalse(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(),
            new CertificateUploadRequest("expired", cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem()), now,
            out _));
    }

    [TestMethod]
    public void TryCreate_NotYetValidCertificate_IsRejected()
    {
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=future.example", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(now.AddDays(1), now.AddDays(2));

        Assert.IsFalse(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(),
            new CertificateUploadRequest("future", cert.ExportCertificatePem(), key.ExportPkcs8PrivateKeyPem()), now,
            out _));
    }

    [TestMethod]
    public void TryCreate_PathAndAcmeSources_ValidateRequiredFields()
    {
        var now = DateTimeOffset.Parse("2026-09-23T00:00:00Z");
        Assert.IsTrue(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(), new CertificateUploadRequest("le", null, null,
            "Path", "/etc/letsencrypt/live/a.example.com/fullchain.pem", "/etc/letsencrypt/live/a.example.com/privkey.pem",
            "A.Example.com"), now, out var path));
        Assert.AreEqual("DNS:a.example.com", path.San);
        Assert.IsFalse(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(), new CertificateUploadRequest("bad", null, null,
            "Path", "relative/cert.pem", "/key.pem", "a.example.com"), now, out _));

        Assert.IsTrue(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(), new CertificateUploadRequest("acme", null, null,
            "Acme", Domain: "hy.example.com", AcmeEmail: "ops@example.com", AcmeChallenge: "http"), now, out var http));
        Assert.IsNull(http.AcmeDnsToken);
        Assert.IsFalse(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(), new CertificateUploadRequest("dns", null, null,
            "Acme", Domain: "hy.example.com", AcmeEmail: "ops@example.com", AcmeChallenge: "cloudflare"), now, out _),
            "Cloudflare DNS needs an API token");
        Assert.IsFalse(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(), new CertificateUploadRequest("wild", null, null,
            "Acme", Domain: "*.example.com", AcmeEmail: "ops@example.com", AcmeChallenge: "http"), now, out _));
    }
}
