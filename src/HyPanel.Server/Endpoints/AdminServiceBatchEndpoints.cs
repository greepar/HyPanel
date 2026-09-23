namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;

internal static class AdminServiceBatchEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/admin/v1/services/batch-enabled", SetBatchEnabledAsync);

    private static async Task<IResult> SetBatchEnabledAsync(BatchServiceEnabledRequest request, HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (request.Items is null || request.Items.Length is < 1 or > 256 ||
            request.Items.Select(static item => item.ServiceId).Distinct().Count() != request.Items.Length)
            return Results.BadRequest();
        var result = await repository.SetServicesEnabledBatchAsync(request.Items.Select(static item =>
            new BatchServiceEnabledItemRecord(item.NodeId, item.ServiceId, item.Enabled)).ToArray(), ct);
        if (result is null) return Results.NotFound();
        return Results.Json(new BatchServiceEnabledResponse(result.Select(static item =>
                new BatchServiceEnabledNodeResponse(item.NodeId, item.Revision)).ToArray()),
            ServerJsonSerializerContext.Default.BatchServiceEnabledResponse);
    }
}
