namespace HyPanel.Server.Endpoints;

using System.Security.Cryptography;
using System.Text;
using HyPanel.Server.Persistence;
using Microsoft.Data.Sqlite;

internal static class UserEndpoints
{
    internal const long MaximumBrowserSafeBytes = 9_007_199_254_740_991;
    internal const int MinimumPasswordLength = 6;
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/v1/login", LoginAsync).RequireRateLimiting("login");
        endpoints.MapPost("/api/auth/v1/logout", LogoutAsync);
        endpoints.MapGet("/api/auth/v1/setup", SetupStatusAsync);
        endpoints.MapPost("/api/auth/v1/setup", SetupAsync).RequireRateLimiting("login");
        endpoints.MapGet("/api/user/v1/me", MeAsync);
        endpoints.MapGet("/api/admin/v1/users", GetUsersAsync);
        endpoints.MapPost("/api/admin/v1/users", CreateUserAsync);
        endpoints.MapPut("/api/admin/v1/users/{id:guid}", UpdateUserAsync);
        endpoints.MapDelete("/api/admin/v1/users/{id:guid}", DeleteUserAsync);
        endpoints.MapPost("/api/admin/v1/users/{id:guid}/subscription-token/rotate", RotateAsync);
        endpoints.MapGet("/api/admin/v1/users/{id:guid}/subscription-token", GetTokenAsync);
        endpoints.MapGet("/api/admin/v1/users/{id:guid}/services", GetServicesAsync);
        endpoints.MapGet("/api/admin/v1/users/{id:guid}/service-access", GetServiceAccessAsync);
        endpoints.MapPut("/api/admin/v1/users/{id:guid}/services/{serviceId:guid}", BindAsync);
        endpoints.MapDelete("/api/admin/v1/users/{id:guid}/services/{serviceId:guid}", UnbindAsync);
        endpoints.MapPost("/api/admin/v1/users/{id:guid}/services/{serviceId:guid}/credential/rotate", RotateCredentialAsync);
        endpoints.MapPost("/api/admin/v1/users/{id:guid}/services/{serviceId:guid}/credential/revoke", RevokeCredentialAsync);
        endpoints.MapGet("/api/admin/v1/usage", GetAdminUsageAsync);
        endpoints.MapGet("/api/user/v1/usage", GetUserUsageAsync);
        endpoints.MapPost("/api/user/v1/subscription-token/rotate", RotateOwnSubscriptionTokenAsync);
        endpoints.MapGet("/api/user/v1/subscription-token", GetOwnTokenAsync);
    }

    private static async Task<IResult> LoginAsync(LoginRequest request, SqliteServerRepository repository,
        PasswordService passwords, TimeProvider time, CancellationToken ct)
    {
        if (!TryUsername(request.Username, out _, out var normalized) ||
            request.Password is not { Length: >= 1 and <= 256 }) return Results.Unauthorized();
        var (user, hash) = await repository.GetLoginUserAsync(normalized, ct);
        if (user is null || hash is null || !user.Enabled || user.ExpiresAtUtc <= time.GetUtcNow() ||
            !passwords.Verify(request.Password, hash)) return Results.Unauthorized();
        return await IssueSessionAsync(user, repository, time, ct);
    }

    private static async Task<IResult> SetupStatusAsync(SqliteServerRepository repository, CancellationToken ct) =>
        Results.Json(new SetupStatusResponse((await repository.GetUsersAsync(ct)).Count == 0),
            ServerJsonSerializerContext.Default.SetupStatusResponse);

    /// <summary>First run only: creates the initial administrator and signs it in.</summary>
    private static async Task<IResult> SetupAsync(SetupRequest request, AdminTokenAuthentication bootstrap,
        SqliteServerRepository repository, PasswordService passwords, TimeProvider time, CancellationToken ct)
    {
        if (!bootstrap.Matches(request.Token)) return Results.Unauthorized();
        if ((await repository.GetUsersAsync(ct)).Count != 0) return Results.Conflict();
        if (!TryUsername(request.Username, out var username, out var normalized) ||
            request.Password is not { Length: >= MinimumPasswordLength and <= 256 }) return Results.BadRequest();
        var issue = await repository.CreateUserAsync(Guid.NewGuid(), username, normalized, passwords.Hash(request.Password),
            "Admin", true, null, null, NewToken(), ct);
        await repository.SetUserGroupAsync(issue.User.Id, SqliteServerRepository.DefaultGroupId, ct);
        return await IssueSessionAsync(issue.User, repository, time, ct);
    }

    internal static async Task<IResult> IssueSessionAsync(UserRecord user, SqliteServerRepository repository,
        TimeProvider time, CancellationToken ct)
    {
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
        var groups = await repo.GetUserGroupMapAsync(ct);
        return Results.Json((await repo.GetUsersAsync(ct))
                .Select(user => ToResponse(user) with { GroupId = groups.GetValueOrDefault(user.Id) }).ToArray(),
            ServerJsonSerializerContext.Default.UserResponseArray);
    }

    private static async Task<IResult> CreateUserAsync(HttpRequest request, CreateUserRequest body,
        AdminAuthorization authorization, SqliteServerRepository repo,
        PasswordService passwords, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!TryUsername(body.Username, out var username, out var normalized) ||
            body.Password is not { Length: >= MinimumPasswordLength and <= 256 } || body.Role is not ("Admin" or "User") ||
            !IsValidTrafficLimit(body.TrafficLimitBytes) || body.TrafficResetDay is not (null or >= 1 and <= 28))
            return Results.BadRequest();
        try
        {
            var issue = await repo.CreateUserAsync(Guid.NewGuid(), username, normalized, passwords.Hash(body.Password),
                body.Role, body.Enabled, body.TrafficLimitBytes, body.ExpiresAtUtc, NewToken(), ct);
            // Every user belongs to a group; its service list decides what the user can use.
            if (!await repo.SetUserGroupAsync(issue.User.Id, body.GroupId ?? SqliteServerRepository.DefaultGroupId, ct))
                await repo.SetUserGroupAsync(issue.User.Id, SqliteServerRepository.DefaultGroupId, ct);
            await repo.SetTrafficResetDayAsync(issue.User.Id, body.TrafficResetDay, ct);
            return Results.Json(new CreateUserResponse(ToResponse(issue.User with { TrafficResetDay = body.TrafficResetDay }), issue.SubscriptionToken),
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
            body.Password is not null and not { Length: >= MinimumPasswordLength and <= 256 } || body.Role is not ("Admin" or "User") ||
            !IsValidTrafficLimit(body.TrafficLimitBytes) || body.TrafficResetDay is not (null or >= 1 and <= 28))
            return Results.BadRequest();
        try
        {
            var user = await repo.UpdateUserAsync(id, username, normalized,
                body.Password is null ? null : passwords.Hash(body.Password), body.Role, body.Enabled,
                body.TrafficLimitBytes, body.ExpiresAtUtc, ct);
            if (user is null) return Results.NotFound();
            if (body.GroupId is { } groupId && !await repo.SetUserGroupAsync(id, groupId, ct)) return Results.BadRequest();
            await repo.SetTrafficResetDayAsync(id, body.TrafficResetDay, ct);
            return Results.Json(ToResponse(user with { TrafficResetDay = body.TrafficResetDay }) with { GroupId = body.GroupId },
                ServerJsonSerializerContext.Default.UserResponse);
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
        var token = await r.ResetSubscriptionAsync(id, NewToken(), ct);
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

    private static async Task<IResult> GetServiceAccessAsync(Guid id, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var bound = await repository.GetBoundServicesAsync(id, ct);
        var rows = new List<UserServiceAccessResponse>(bound.Count);
        foreach (var serviceId in bound)
        {
            var credential = (await repository.GetServiceCredentialsAsync(serviceId, false, ct))
                .SingleOrDefault(item => item.UserId == id);
            rows.Add(credential is null
                ? new UserServiceAccessResponse(serviceId, "Unsupported", false, false, false)
                : new UserServiceAccessResponse(serviceId, credential.Status, true, true, true));
        }
        return Results.Json(rows.ToArray(), ServerJsonSerializerContext.Default.UserServiceAccessResponseArray);
    }

    private static async Task<IResult> UnbindAsync(Guid id, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository r, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await r.UnbindServiceAsync(id, serviceId, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RotateCredentialAsync(Guid id, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var credential = await repository.RotateServiceCredentialAsync(id, serviceId, ct);
        return credential is null ? Results.NotFound() : Results.NoContent();
    }

    private static async Task<IResult> RevokeCredentialAsync(Guid id, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var credential = await repository.RevokeServiceCredentialAsync(id, serviceId, ct);
        return credential is null ? Results.NotFound() : Results.NoContent();
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

    /// <summary>Current subscription token; 404 when it predates recoverable tokens and must be rotated once.</summary>
    private static async Task<IResult> GetTokenAsync(Guid id, HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository r, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var token = await r.GetSubscriptionTokenAsync(id, ct);
        return token is null
            ? Results.NotFound()
            : Results.Json(new RotateSubscriptionTokenResponse(token),
                ServerJsonSerializerContext.Default.RotateSubscriptionTokenResponse);
    }

    private static async Task<IResult> GetOwnTokenAsync(HttpRequest request, UserAuthentication auth,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        if (user is null) return Results.Unauthorized();
        var token = await repository.GetSubscriptionTokenAsync(user.Id, ct);
        return token is null
            ? Results.NotFound()
            : Results.Json(new RotateSubscriptionTokenResponse(token),
                ServerJsonSerializerContext.Default.RotateSubscriptionTokenResponse);
    }

    private static async Task<IResult> RotateOwnSubscriptionTokenAsync(HttpRequest request, UserAuthentication auth,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var user = await auth.AuthenticateAsync(request, ct);
        if (user is null) return Results.Unauthorized();
        var token = await repository.ResetSubscriptionAsync(user.Id, NewToken(), ct);
        return token is null
            ? Results.Unauthorized()
            : Results.Json(new RotateSubscriptionTokenResponse(token),
                ServerJsonSerializerContext.Default.RotateSubscriptionTokenResponse);
    }

    internal static bool IsValidTrafficLimit(long? value) => value is null or >= 0 and <= MaximumBrowserSafeBytes;

    private static bool TryUsername(string? input, out string username, out string normalized)
    {
        username = (input ?? string.Empty).Normalize(NormalizationForm.FormKC).Trim();
        normalized = username.ToLowerInvariant();
        return username.Length is >= 3 and <= 64;
    }

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=')
        .Replace('+', '-').Replace('/', '_');

    private static UserResponse ToResponse(UserRecord u) =>
        new(u.Id, u.Username, u.Role, u.Enabled, u.TrafficLimitBytes, u.ExpiresAtUtc, TrafficResetDay: u.TrafficResetDay);

    private static UsageTotalResponse ToResponse(UsageTotalRecord u) =>
        new(u.UserId, u.ServiceId, u.UploadBytes, u.DownloadBytes, u.UpdatedAtUtc);
}
