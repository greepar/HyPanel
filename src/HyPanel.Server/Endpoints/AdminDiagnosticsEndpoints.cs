namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;

internal static class AdminDiagnosticsEndpoints
{
    private static readonly TimeSpan CommandLifetime = TimeSpan.FromMinutes(2);

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/diagnostics/logs", CreateAsync);
        endpoints.MapGet("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/diagnostics", ListAsync);
    }

    private static async Task<IResult> CreateAsync(Guid nodeId, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(request, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var serviceExists = (await repository.GetServicesForNodeAsync(nodeId, cancellationToken))
            .Any(item => item.Service.Id == serviceId);
        if (!serviceExists) return Results.NotFound();
        var command = await repository.CreateCollectServiceLogsCommandAsync(Guid.NewGuid(), nodeId, serviceId,
            time.GetUtcNow() + CommandLifetime, cancellationToken);
        return command is null
            ? Results.Conflict()
            : Results.Json(new CreateDiagnosticCommandResponse(command.Id),
                ServerJsonSerializerContext.Default.CreateDiagnosticCommandResponse,
                statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListAsync(Guid nodeId, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, TimeProvider time,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(request, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var serviceExists = (await repository.GetServicesForNodeAsync(nodeId, cancellationToken))
            .Any(item => item.Service.Id == serviceId);
        if (!serviceExists) return Results.NotFound();
        var records = await repository.GetServiceDiagnosticsAsync(nodeId, serviceId, cancellationToken);
        var now = time.GetUtcNow();
        var response = records.Select(command => new ServiceDiagnosticResponse(
            command.Id, EffectiveStatus(command, now), command.CreatedAtUtc, command.StartedAtUtc, command.CompletedAtUtc,
            command.ExpiresAtUtc,
            command.ErrorCode, command.ErrorMessage, command.Output)).ToArray();
        return Results.Json(response, ServerJsonSerializerContext.Default.ServiceDiagnosticResponseArray);
    }

    internal static string EffectiveStatus(AgentCommandRecord command, DateTimeOffset now) =>
        command.Status is "Pending" or "Running" && command.ExpiresAtUtc is { } expiresAt && expiresAt <= now
            ? "Expired"
            : command.Status;
}
