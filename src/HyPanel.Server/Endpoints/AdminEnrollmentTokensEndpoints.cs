namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;

internal static class AdminEnrollmentTokensEndpoints
{
    private static readonly TimeSpan EnrollmentTokenLifetime = TimeSpan.FromMinutes(15);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/enrollment-tokens", CreateAsync);
    }

    private static async Task<IResult> CreateAsync(
        Guid nodeId,
        HttpRequest httpRequest,
        AdminAuthorization authorization,
        SqliteServerRepository repository,
        EnrollmentService enrollmentService,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);

        if (await repository.GetNodeAsync(nodeId, cancellationToken) is null)
        {
            return Results.NotFound();
        }

        var issue = await enrollmentService.IssueTokenAsync(nodeId, EnrollmentTokenLifetime, cancellationToken);
        var response = new CreateEnrollmentTokenResponse(issue.PlaintextToken, issue.Token.ExpiresAtUtc);
        return Results.Json(response, ServerJsonSerializerContext.Default.CreateEnrollmentTokenResponse);
    }
}