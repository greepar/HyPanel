using System.Formats.Asn1;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyPanel.Agent.Networking;
using HyPanel.Shared.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests.Networking;

[TestClass]
public sealed class AcmeChallengeResponderTests
{
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [TestMethod]
    public async Task Http01_ServesTheKeyAuthorizationOnlyWhileListed()
    {
        var port = FreePort();
        using var responder = new AcmeChallengeResponder(httpPort: port, tlsPort: FreePort());
        responder.Apply([new AcmeChallengeAssignment(AcmeChallengeResponder.Http01, "hy.example.com", "tok-1", "tok-1.thumb")]);
        Assert.IsTrue(responder.Reports().Single().Ready);
        CollectionAssert.AreEqual(new[] { new FirewallOpening("tcp", port, null) }, responder.Openings.ToArray());

        using var http = new HttpClient();
        Assert.AreEqual("tok-1.thumb", await http.GetStringAsync($"http://127.0.0.1:{port}/.well-known/acme-challenge/tok-1"));
        var missing = await http.GetAsync($"http://127.0.0.1:{port}/.well-known/acme-challenge/other");
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);

        responder.Apply([]);
        Assert.AreEqual(0, responder.Openings.Count, "the port is released when nothing is listed");
        await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            http.GetStringAsync($"http://127.0.0.1:{port}/.well-known/acme-challenge/tok-1"));
    }

    [TestMethod]
    public async Task TlsAlpn01_PresentsTheChallengeCertificateToAcmeTlsClients()
    {
        var port = FreePort();
        using var responder = new AcmeChallengeResponder(httpPort: FreePort(), tlsPort: port);
        responder.Apply([new AcmeChallengeAssignment(AcmeChallengeResponder.TlsAlpn01, "tls.example.com", "tok-2", "tok-2.thumb")]);
        Assert.IsTrue(responder.Reports().Single().Ready);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var ssl = new SslStream(client.GetStream());
        X509Certificate2? presented = null;
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "tls.example.com",
            ApplicationProtocols = [new SslApplicationProtocol("acme-tls/1")],
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                presented = new X509Certificate2(certificate!);
                return true;
            }
        });

        Assert.AreEqual("acme-tls/1", ssl.NegotiatedApplicationProtocol.ToString());
        var extension = presented!.Extensions["1.3.6.1.5.5.7.1.31"];
        Assert.IsNotNull(extension);
        Assert.IsTrue(extension.Critical);
        CollectionAssert.AreEqual(SHA256.HashData(Encoding.ASCII.GetBytes("tok-2.thumb")),
            new AsnReader(extension.RawData, AsnEncodingRules.DER).ReadOctetString());
        CollectionAssert.AreEqual(new[] { "tls.example.com" },
            presented.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames().ToArray());
    }

    [TestMethod]
    public void ATakenPortIsReportedInsteadOfReady()
    {
        using var other = new TcpListener(IPAddress.IPv6Any, 0) { Server = { DualMode = true } };
        other.Start();
        var port = ((IPEndPoint)other.LocalEndpoint).Port;
        using var responder = new AcmeChallengeResponder(httpPort: port, tlsPort: FreePort());

        responder.Apply([new AcmeChallengeAssignment(AcmeChallengeResponder.Http01, "hy.example.com", "tok-3", "tok-3.thumb")]);

        var report = responder.Reports().Single();
        Assert.IsFalse(report.Ready);
        StringAssert.Contains(report.Error, $"TCP {port}");
    }

    [TestMethod]
    public void MalformedChallengesAreIgnored()
    {
        using var responder = new AcmeChallengeResponder(httpPort: FreePort(), tlsPort: FreePort());

        responder.Apply([
            new AcmeChallengeAssignment(AcmeChallengeResponder.Http01, "hy.example.com", "../etc", "../etc.x"),
            new AcmeChallengeAssignment(AcmeChallengeResponder.TlsAlpn01, "*.example.com", "tok", "tok.x"),
            new AcmeChallengeAssignment("dns-01", "hy.example.com", "tok", "tok.x")
        ]);

        Assert.AreEqual(0, responder.Reports().Count);
        Assert.AreEqual(0, responder.Openings.Count);
    }
}
