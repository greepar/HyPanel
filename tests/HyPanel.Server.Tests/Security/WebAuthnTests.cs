namespace HyPanel.Server.Tests.Security;

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HyPanel.Server.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class WebAuthnTests
{
    private const string RpId = "panel.example.com";

    [TestMethod]
    public void Registration_AcceptsMatchingCredentialAndRejectsOtherOrigin()
    {
        var challenge = RandomNumberGenerator.GetBytes(32);
        var credentialId = RandomNumberGenerator.GetBytes(20);
        var authData = AuthData(RpId, 0, credentialId);

        Assert.IsTrue(WebAuthn.VerifyClientData(ClientData("webauthn.create", challenge, $"https://{RpId}"),
            "webauthn.create", challenge, RpId));
        Assert.IsTrue(WebAuthn.VerifyAuthenticatorData(authData, RpId, credentialId, out _));
        Assert.IsFalse(WebAuthn.VerifyAuthenticatorData(authData, RpId, RandomNumberGenerator.GetBytes(20), out _));
        Assert.IsFalse(WebAuthn.VerifyClientData(ClientData("webauthn.create", challenge, "https://evil.example.com"),
            "webauthn.create", challenge, RpId));
        Assert.IsFalse(WebAuthn.VerifyClientData(ClientData("webauthn.create", challenge, $"http://{RpId}"),
            "webauthn.create", challenge, RpId));
        Assert.IsFalse(WebAuthn.VerifyClientData(ClientData("webauthn.get", challenge, $"https://{RpId}"),
            "webauthn.create", challenge, RpId));
    }

    [TestMethod]
    public void Assertion_Es256SignatureVerifiesAndTamperingFails()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfo();
        Assert.IsTrue(WebAuthn.IsSupportedKey(publicKey, WebAuthn.Es256));
        Assert.IsFalse(WebAuthn.IsSupportedKey(publicKey, WebAuthn.Rs256));

        var challenge = RandomNumberGenerator.GetBytes(32);
        var clientData = ClientData("webauthn.get", challenge, $"https://{RpId}");
        var authData = AuthData(RpId, 7, null);
        var signature = key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);

        Assert.IsTrue(WebAuthn.VerifyAuthenticatorData(authData, RpId, null, out var count));
        Assert.AreEqual(7u, count);
        Assert.IsTrue(WebAuthn.VerifySignature(publicKey, WebAuthn.Es256, authData, clientData, signature));
        authData[34] ^= 1;
        Assert.IsFalse(WebAuthn.VerifySignature(publicKey, WebAuthn.Es256, authData, clientData, signature));
        Assert.IsFalse(WebAuthn.VerifyAuthenticatorData(AuthData("other.example.com", 0, null), RpId, null, out _));
    }

    [TestMethod]
    public void Challenges_AreSingleUseBoundToUserAndExpire()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var webAuthn = new WebAuthn(time);
        var user = Guid.NewGuid();

        var first = webAuthn.CreateChallenge(user)!.Value;
        Assert.IsNull(webAuthn.TakeChallenge(first.Id, null));
        var second = webAuthn.CreateChallenge(user)!.Value;
        CollectionAssert.AreEqual(WebAuthn.Decode(second.Value), webAuthn.TakeChallenge(second.Id, user));
        Assert.IsNull(webAuthn.TakeChallenge(second.Id, user));

        var third = webAuthn.CreateChallenge(null)!.Value;
        time.Advance(TimeSpan.FromMinutes(6));
        Assert.IsNull(webAuthn.TakeChallenge(third.Id, null));
    }

    private static byte[] ClientData(string type, byte[] challenge, string origin) => Encoding.UTF8.GetBytes(
        $$"""{"type":"{{type}}","challenge":"{{WebAuthn.Encode(challenge)}}","origin":"{{origin}}","crossOrigin":false}""");

    private static byte[] AuthData(string rpId, uint signCount, byte[]? credentialId)
    {
        var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)));
        data.Add((byte)(0x01 | 0x04 | (credentialId is null ? 0 : 0x40)));
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, signCount);
        data.AddRange(counter);
        if (credentialId is not null)
        {
            data.AddRange(new byte[16]);
            data.Add((byte)(credentialId.Length >> 8));
            data.Add((byte)credentialId.Length);
            data.AddRange(credentialId);
        }
        return [.. data];
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
    }
}
