namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;
using HyPanel.Server.Security;
using Microsoft.Data.Sqlite;

/// <summary>Passkey (WebAuthn) registration for signed-in accounts and passwordless sign-in.</summary>
internal static class PasskeyEndpoints
{
    private const string RpName = "HyPanel";
    private const int MaxPasskeysPerUser = 20;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/user/v1/passkeys", ListAsync);
        endpoints.MapPost("/api/user/v1/passkeys/options", RegistrationOptionsAsync);
        endpoints.MapPost("/api/user/v1/passkeys", RegisterAsync);
        endpoints.MapDelete("/api/user/v1/passkeys/{id}", DeleteAsync);
        endpoints.MapPost("/api/auth/v1/passkey/options", LoginOptions).RequireRateLimiting("login");
        endpoints.MapPost("/api/auth/v1/passkey/login", LoginAsync).RequireRateLimiting("login");
    }

    private static async Task<IResult> ListAsync(HttpRequest request, UserAuthentication auth,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        if (user is null) return Results.Unauthorized();
        return Results.Json((await repository.GetPasskeysAsync(user.Id, ct)).Select(ToResponse).ToArray(),
            ServerJsonSerializerContext.Default.PasskeyResponseArray);
    }

    private static async Task<IResult> RegistrationOptionsAsync(HttpRequest request, UserAuthentication auth,
        SqliteServerRepository repository, WebAuthn webAuthn, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        if (user is null) return Results.Unauthorized();
        if (webAuthn.CreateChallenge(user.Id) is not { } challenge) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var existing = await repository.GetPasskeysAsync(user.Id, ct);
        return Results.Json(new PasskeyOptionsResponse(challenge.Id, challenge.Value, WebAuthn.RpId(request), RpName,
                WebAuthn.Encode(user.Id.ToByteArray()), user.Username, existing.Select(key => key.Id).ToArray()),
            ServerJsonSerializerContext.Default.PasskeyOptionsResponse);
    }

    private static async Task<IResult> RegisterAsync(HttpRequest request, PasskeyRegisterRequest body,
        UserAuthentication auth, SqliteServerRepository repository, WebAuthn webAuthn, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        if (user is null) return Results.Unauthorized();
        var rpId = WebAuthn.RpId(request);
        if (webAuthn.TakeChallenge(body.ChallengeId, user.Id) is not { } challenge)
            return Error("验证已过期，请重试。");
        if (WebAuthn.Decode(body.CredentialId) is not { Length: > 0 and <= 1023 } credentialId ||
            WebAuthn.Decode(body.ClientDataJson) is not { } clientData ||
            WebAuthn.Decode(body.AuthenticatorData) is not { } authData ||
            WebAuthn.Decode(body.PublicKey) is not { } publicKey)
            return Error("通行密钥数据不完整。");
        if (!WebAuthn.VerifyClientData(clientData, "webauthn.create", challenge, rpId) ||
            !WebAuthn.VerifyAuthenticatorData(authData, rpId, credentialId, out var signCount))
            return Error("通行密钥验证失败，请确认通过面板域名访问。");
        if (!WebAuthn.IsSupportedKey(publicKey, body.Algorithm))
            return Error("不支持此通行密钥的算法。");
        if ((await repository.GetPasskeysAsync(user.Id, ct)).Count >= MaxPasskeysPerUser)
            return Error($"每个账户最多 {MaxPasskeysPerUser} 个通行密钥。");

        var name = (body.Name ?? string.Empty).Trim();
        if (name.Length == 0) name = "通行密钥";
        if (name.Length > 64) name = name[..64];
        try
        {
            var passkey = await repository.AddPasskeyAsync(user.Id, WebAuthn.Encode(credentialId), name, publicKey,
                body.Algorithm, signCount, ct);
            return Results.Json(ToResponse(passkey), ServerJsonSerializerContext.Default.PasskeyResponse);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Results.Conflict();
        }
    }

    private static async Task<IResult> DeleteAsync(string id, HttpRequest request, UserAuthentication auth,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        if (user is null) return Results.Unauthorized();
        return await repository.DeletePasskeyAsync(user.Id, id, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static IResult LoginOptions(HttpRequest request, WebAuthn webAuthn) =>
        webAuthn.CreateChallenge(null) is { } challenge
            ? Results.Json(new PasskeyOptionsResponse(challenge.Id, challenge.Value, WebAuthn.RpId(request), RpName,
                null, null, []), ServerJsonSerializerContext.Default.PasskeyOptionsResponse)
            : Results.StatusCode(StatusCodes.Status429TooManyRequests);

    private static async Task<IResult> LoginAsync(HttpRequest request, PasskeyLoginRequest body,
        SqliteServerRepository repository, WebAuthn webAuthn, TimeProvider time, CancellationToken ct)
    {
        var rpId = WebAuthn.RpId(request);
        if (webAuthn.TakeChallenge(body.ChallengeId, null) is not { } challenge ||
            WebAuthn.Decode(body.CredentialId) is not { } credentialId ||
            WebAuthn.Decode(body.ClientDataJson) is not { } clientData ||
            WebAuthn.Decode(body.AuthenticatorData) is not { } authData ||
            WebAuthn.Decode(body.Signature) is not { } signature ||
            await repository.GetPasskeyAsync(WebAuthn.Encode(credentialId), ct) is not { } passkey)
            return Results.Unauthorized();
        if (body.UserHandle is { Length: > 0 } &&
            (WebAuthn.Decode(body.UserHandle) is not { Length: 16 } handle || new Guid(handle) != passkey.UserId))
            return Results.Unauthorized();
        if (!WebAuthn.VerifyClientData(clientData, "webauthn.get", challenge, rpId) ||
            !WebAuthn.VerifyAuthenticatorData(authData, rpId, null, out var signCount) ||
            !WebAuthn.VerifySignature(passkey.PublicKey, passkey.Algorithm, authData, clientData, signature))
            return Results.Unauthorized();
        // A counter that fails to advance indicates a cloned authenticator; synced passkeys always report 0.
        if ((signCount != 0 || passkey.SignCount != 0) && signCount <= passkey.SignCount) return Results.Unauthorized();

        var user = await repository.GetUserAsync(passkey.UserId, ct);
        if (user is null || !user.Enabled || user.ExpiresAtUtc <= time.GetUtcNow()) return Results.Unauthorized();
        await repository.MarkPasskeyUsedAsync(passkey.Id, signCount, ct);
        return await UserEndpoints.IssueSessionAsync(user, repository, time, ct);
    }

    private static IResult Error(string message) =>
        Results.Json(new CertificateErrorResponse(message), ServerJsonSerializerContext.Default.CertificateErrorResponse,
            statusCode: StatusCodes.Status400BadRequest);

    private static PasskeyResponse ToResponse(PasskeyRecord passkey) =>
        new(passkey.Id, passkey.Name, passkey.CreatedAtUtc, passkey.LastUsedAtUtc);
}
