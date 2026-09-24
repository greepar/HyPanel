namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Updates;

internal static class ServerUpdateEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/server-update", GetAsync);
        endpoints.MapPost("/api/admin/v1/server-update", ApplyAsync);
    }

    private static async Task<IResult> GetAsync(HttpRequest request, AdminAuthorization authorization,
        ServerUpdateService updater, CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(request, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        await updater.RefreshAsync(cancellationToken);
        return Results.Json(updater.GetStatus(), ServerJsonSerializerContext.Default.ServerUpdateStatus);
    }

    private static async Task<IResult> ApplyAsync(HttpRequest request, AdminAuthorization authorization,
        ServerUpdateService updater, CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(request, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        // The cached release may predate a new tag (it refreshes periodically or on GET); re-check once.
        if (!updater.CanUpdate(out _))
        {
            try { await updater.RefreshAsync(cancellationToken); }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException) { }
        }
        if (!updater.CanUpdate(out var target)) return Results.Conflict();
        updater.QueueUpdate();
        return Results.Json(new ServerUpdateRequestResponse(target!),
            ServerJsonSerializerContext.Default.ServerUpdateRequestResponse, statusCode: StatusCodes.Status202Accepted);
    }
}
