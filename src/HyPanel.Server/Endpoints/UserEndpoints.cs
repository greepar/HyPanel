namespace HyPanel.Server.Endpoints;

using System.Security.Cryptography;
using System.Text;
using HyPanel.Server.Persistence;
using Microsoft.Data.Sqlite;

internal static class UserEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/v1/login", LoginAsync);
        endpoints.MapPost("/api/auth/v1/logout", LogoutAsync);
        endpoints.MapGet("/api/user/v1/me", MeAsync);
        endpoints.MapGet("/api/admin/v1/users", GetUsersAsync);
        endpoints.MapPost("/api/admin/v1/users", CreateUserAsync);
        endpoints.MapPut("/api/admin/v1/users/{id:guid}", UpdateUserAsync);
        endpoints.MapDelete("/api/admin/v1/users/{id:guid}", DeleteUserAsync);
        endpoints.MapPost("/api/admin/v1/users/{id:guid}/subscription-token/rotate", RotateAsync);
        endpoints.MapGet("/api/admin/v1/users/{id:guid}/services", GetServicesAsync);
        endpoints.MapPut("/api/admin/v1/users/{id:guid}/services/{serviceId:guid}", BindAsync);
        endpoints.MapDelete("/api/admin/v1/users/{id:guid}/services/{serviceId:guid}", UnbindAsync);
        endpoints.MapGet("/api/admin/v1/usage", GetAdminUsageAsync);
        endpoints.MapGet("/api/user/v1/usage", GetUserUsageAsync);
        endpoints.MapPost("/api/user/v1/subscription-token/rotate", RotateOwnSubscriptionTokenAsync);
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, SqliteServerRepository repository,
        PasswordService passwords, TimeProvider time, CancellationToken ct)
    {
        if (!TryUsername(request.Username, out _, out var normalized) ||
            request.Password is not { Length: >= 12 and <= 256 }) return Results.Unauthorized();
        var (user, hash) = await repository.GetLoginUserAsync(normalized, ct);
        if (user is null || hash is null || !user.Enabled || user.ExpiresAtUtc <= time.GetUtcNow() ||
            !passwords.Verify(request.Password, hash)) return Results.Unauthorized();
        var expiry = time.GetUtcNow().AddHours(24);
        var issue = await repository.CreateSessionAsync(user, NewToken(), expiry, ct);
        return Results.Json(new LoginResponse(issue.Token, issue.ExpiresAtUtc, ToResponse(issue.User)),
            ServerJsonSerializerContext.Default.LoginResponse);
    }

    private static async Task<IResult> LogoutAsync(HttpRequest request, SqliteServerRepository repository,
        CancellationToken ct)
    {
        var token = UserAuthentication.GetBearer(request);
        if (token is not null) await repository.RevokeSessionAsync(token, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MeAsync(HttpRequest request, UserAuthentication auth, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Json(ToResponse(user), ServerJsonSerializerContext.Default.UserResponse);
    }

    private static async Task<IResult> GetUsersAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repo, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Results.Json((await repo.GetUsersAsync(ct)).Select(ToResponse).ToArray(),
            ServerJsonSerializerContext.Default.UserResponseArray);
    }

    private static async Task<IResult> CreateUserAsync(HttpRequest request, CreateUserRequest body,
        AdminAuthorization authorization, SqliteServerRepository repo,
        PasswordService passwords, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!TryUsername(body.Username, out var username, out var normalized) ||
            body.Password is not { Length: >= 12 and <= 256 } || body.Role is not ("Admin" or "User") ||
            body.TrafficLimitBytes < 0) return Results.BadRequest();
        try
        {
            var issue = await repo.CreateUserAsync(Guid.NewGuid(), username, normalized, passwords.Hash(body.Password),
                body.Role, body.Enabled, body.TrafficLimitBytes, body.ExpiresAtUtc, NewToken(), ct);
            return Results.Json(new CreateUserResponse(ToResponse(issue.User), issue.SubscriptionToken),
                ServerJsonSerializerContext.Default.CreateUserResponse);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Results.Conflict();
        }
    }

    private static async Task<IResult> UpdateUserAsync(Guid id, HttpRequest request, UpdateUserRequest body,
        AdminAuthorization authorization, SqliteServerRepository repo,
        PasswordService passwords, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!TryUsername(body.Username, out var username, out var normalized) ||
            body.Password is not null and not { Length: >= 12 and <= 256 } || body.Role is not ("Admin" or "User") ||
            body.TrafficLimitBytes < 0) return Results.BadRequest();
        try
        {
            var user = await repo.UpdateUserAsync(id, username, normalized,
                body.Password is null ? null : passwords.Hash(body.Password), body.Role, body.Enabled,
                body.TrafficLimitBytes, body.ExpiresAtUtc, ct);
            return user is null
                ? Results.NotFound()
                : Results.Json(ToResponse(user), ServerJsonSerializerContext.Default.UserResponse);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return Results.Conflict();
        }
    }

    private static async Task<IResult> DeleteUserAsync(Guid id, HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository r, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await r.DeleteUserAsync(id, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RotateAsync(Guid id, HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository r, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var token = await r.RotateSubscriptionTokenAsync(id, NewToken(), ct);
        return token is null
            ? Results.NotFound()
            : Results.Json(new RotateSubscriptionTokenResponse(token),
                ServerJsonSerializerContext.Default.RotateSubscriptionTokenResponse);
    }

    private static async Task<IResult> GetServicesAsync(Guid id, HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository r, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Results.Json((await r.GetBoundServicesAsync(id, ct)).ToArray(),
            ServerJsonSerializerContext.Default.GuidArray);
    }

    private static async Task<IResult> BindAsync(Guid id, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository r, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await r.BindServiceAsync(id, serviceId, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> UnbindAsync(Guid id, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository r, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await r.UnbindServiceAsync(id, serviceId, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetAdminUsageAsync(Guid? userId, Guid? serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository r, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Results.Json((await r.GetUsageTotalsAsync(userId, serviceId, ct)).Select(ToResponse).ToArray(),
            ServerJsonSerializerContext.Default.UsageTotalResponseArray);
    }

    private static async Task<IResult> GetUserUsageAsync(HttpRequest request, UserAuthentication a,
        SqliteServerRepository r, CancellationToken ct)
    {
        var user = await a.AuthenticateAsync(request, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Json((await r.GetUsageTotalsAsync(user.Id, null, ct)).Select(ToResponse).ToArray(),
                ServerJsonSerializerContext.Default.UsageTotalResponseArray);
    }

    private static async Task<IResult> RotateOwnSubscriptionTokenAsync(HttpRequest request, UserAuthentication auth,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        if (user is null) return Results.Unauthorized();
        var token = await repository.RotateSubscriptionTokenAsync(user.Id, NewToken(), ct);
        return token is null
            ? Results.Unauthorized()
            : Results.Json(new RotateSubscriptionTokenResponse(token),
                ServerJsonSerializerContext.Default.RotateSubscriptionTokenResponse);
    }

    private static bool TryUsername(string? input, out string username, out string normalized)
    {
        username = (input ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        normalized = username.ToLowerInvariant();
        return username.Length is >= 3 and <= 64;
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=')
        .Replace('+', '-').Replace('/', '_');

    private static UserResponse ToResponse(UserRecord u) =>
        new(u.Id, u.Username, u.Role, u.Enabled, u.TrafficLimitBytes, u.ExpiresAtUtc);

    private static UsageTotalResponse ToResponse(UsageTotalRecord u) =>
        new(u.UserId, u.ServiceId, u.UploadBytes, u.DownloadBytes, u.UpdatedAtUtc);
}