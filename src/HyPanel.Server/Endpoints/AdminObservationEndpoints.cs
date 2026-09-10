namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;

internal static class AdminObservationEndpoints
{
    private static readonly TimeSpan OnlineThreshold = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthCheckCommandLifetime = TimeSpan.FromMinutes(5);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/nodes", ListNodesAsync);
        endpoints.MapPost("/api/admin/v1/agents/{agentId:guid}/commands/health-check", CreateHealthCheckCommandAsync);
    }

    private static async Task<IResult> ListNodesAsync(
        HttpRequest httpRequest,
        AdminAuthorization authorization,
        SqliteServerRepository repository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);

        var nowUtc = timeProvider.GetUtcNow();
        var observations = await repository.GetNodeObservationsAsync(cancellationToken);
        var response = new AdminNodeObservationResponse[observations.Count];
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            response[index] = new AdminNodeObservationResponse(
                observation.Id,
                observation.DisplayName,
                observation.AgentId,
                observation.LastSeenAtUtc is { } lastSeenAt && lastSeenAt >= nowUtc - OnlineThreshold,
                observation.LastSeenAtUtc,
                observation.ReportedVersion,
                observation.ReportedPlatform,
                observation.DesiredRevision,
                observation.AppliedRevision);
        }

        return Results.Json(response, ServerJsonSerializerContext.Default.AdminNodeObservationResponseArray);
    }

    private static async Task<IResult> CreateHealthCheckCommandAsync(
        Guid agentId,
        HttpRequest httpRequest,
        AdminAuthorization authorization,
        SqliteServerRepository repository,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);

        var expiresAtUtc = timeProvider.GetUtcNow() + HealthCheckCommandLifetime;
        var command = await repository.CreateRunHealthCheckCommandAsync(
            Guid.NewGuid(),
            agentId,
            expiresAtUtc,
            cancellationToken);
        if (command is null)
        {
            return Results.NotFound();
        }

        var response = new CreateHealthCheckCommandResponse(command.Id);
        return Results.Json(
            response,
            ServerJsonSerializerContext.Default.CreateHealthCheckCommandResponse,
            statusCode: StatusCodes.Status201Created);
    }
}