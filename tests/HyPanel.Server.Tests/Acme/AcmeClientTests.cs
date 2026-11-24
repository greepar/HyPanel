using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyPanel.Server.Acme;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Acme;

[TestClass]
public sealed class AcmeClientTests
{
    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    [TestMethod]
    public void RequestsAreSignedES256WithTheJwkFirstAndTheKidAfterwards()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var client = new AcmeClient(new HttpClient(), new Uri("https://acme.example/dir"), key, "https://acme.example/acct/1");

        foreach (var useJwk in new[] { true, false })
        {
            var jws = JsonNode.Parse(client.Sign("https://acme.example/new-order", "nonce-1", new JsonObject { ["a"] = 1 }, useJwk))!;
            var header = JsonNode.Parse(FromBase64Url((string)jws["protected"]!))!;
            Assert.AreEqual("ES256", (string?)header["alg"]);
            Assert.AreEqual("nonce-1", (string?)header["nonce"]);
            Assert.AreEqual("https://acme.example/new-order", (string?)header["url"]);
            Assert.AreEqual(useJwk, header["jwk"] is not null);
            Assert.AreEqual(!useJwk, (string?)header["kid"] == "https://acme.example/acct/1");
            var signed = Encoding.ASCII.GetBytes($"{jws["protected"]}.{jws["payload"]}");
            Assert.IsTrue(key.VerifyData(signed, FromBase64Url((string)jws["signature"]!), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        var get = JsonNode.Parse(client.Sign("https://acme.example/order/1", "n", null, useJwk: false))!;
        Assert.AreEqual("", (string?)get["payload"], "POST-as-GET has an empty payload");
    }

    [TestMethod]
    public void ThumbprintFollowsRfc7638AndKeyAuthorizationsUseIt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var client = new AcmeClient(new HttpClient(), new Uri("https://acme.example/dir"), key, null);
        var parameters = key.ExportParameters(false);
        var canonical = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{AcmeClient.Base64Url(parameters.Q.X)}\",\"y\":\"{AcmeClient.Base64Url(parameters.Q.Y)}\"}}";

        Assert.AreEqual(AcmeClient.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), client.Thumbprint);
        Assert.AreEqual($"tok.{client.Thumbprint}", client.KeyAuthorization("tok"));
        Assert.AreEqual(AcmeClient.Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes($"tok.{client.Thumbprint}"))),
            client.DnsValue("tok"));
    }
}
