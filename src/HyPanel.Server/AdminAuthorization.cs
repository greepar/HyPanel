namespace HyPanel.Server;

internal enum AdminAccessResult
{
    Unauthenticated,
    Forbidden,
    Allowed
}

internal sealed class AdminAuthorization(
    AdminTokenAuthentication bootstrap,
    UserAuthentication users)
{
    public async Task<AdminAccessResult> AuthorizeAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (bootstrap.IsAuthenticated(request)) return AdminAccessResult.Allowed;
        var user = await users.AuthenticateAsync(request, cancellationToken);
        return user is null
            ? AdminAccessResult.Unauthenticated
            : user.Role == "Admin"
                ? AdminAccessResult.Allowed
                : AdminAccessResult.Forbidden;
    }

    public static IResult Failure(AdminAccessResult access) =>
        access == AdminAccessResult.Forbidden
            ? Results.StatusCode(StatusCodes.Status403Forbidden)
            : Results.Unauthorized();
}