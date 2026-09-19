namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Backup;
using Microsoft.AspNetCore.Http.Features;

internal static class AdminBackupEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/backups", ListAsync);
        endpoints.MapPost("/api/admin/v1/backups", CreateAsync);
        endpoints.MapGet("/api/admin/v1/backups/{id}/download", DownloadAsync);
        endpoints.MapDelete("/api/admin/v1/backups/{id}", DeleteAsync);
        endpoints.MapPost("/api/admin/v1/backups/validate", ValidateAsync);
        endpoints.MapPost("/api/admin/v1/backups/restore", RestoreAsync);
    }

    private static async Task<IResult> ListAsync(HttpRequest request, AdminAuthorization authorization,
        BackupRestoreService backups, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Results.Json(await backups.ListAsync(ct), ServerJsonSerializerContext.Default.IReadOnlyListBackupSummary);
    }

    private static async Task<IResult> CreateAsync(HttpRequest request, AdminAuthorization authorization,
        BackupRestoreService backups, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Results.Json(await backups.CreateAsync(ct), ServerJsonSerializerContext.Default.BackupSummary,
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> DownloadAsync(string id, HttpRequest request, HttpResponse response,
        AdminAuthorization authorization, BackupRestoreService backups, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        try
        {
            var lease = backups.OpenDownload(id);
            response.Headers.CacheControl = "no-store";
            response.Headers.Pragma = "no-cache";
            response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(async output =>
            {
                using (lease)
                    await lease.Stream.CopyToAsync(output, request.HttpContext.RequestAborted);
            }, "application/gzip", id);
        }
        catch (FileNotFoundException) { return Results.NotFound(); }
        catch (BackupException exception) { return Error(exception); }
    }

    private static async Task<IResult> DeleteAsync(string id, HttpRequest request,
        AdminAuthorization authorization, BackupRestoreService backups, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        try { return await backups.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(); }
        catch (BackupException exception) { return Error(exception); }
    }

    private static async Task<IResult> ValidateAsync(HttpRequest request, AdminAuthorization authorization,
        BackupRestoreService backups, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var bodySize = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false }) bodySize.MaxRequestBodySize = BackupRestoreService.MaximumArchiveBytes;
        var value = await backups.UploadAndValidateAsync(request.Body, request.ContentLength, ct);
        return Results.Json(value, ServerJsonSerializerContext.Default.BackupValidation);
    }

    private static async Task<IResult> RestoreAsync(RestoreRequest body, HttpRequest request,
        AdminAuthorization authorization, BackupRestoreService backups, ServerProcessControl process,
        CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        try
        {
            await backups.QueueRestoreAsync(body, ct);
            _ = Task.Run(async () =>
            {
                await Task.Delay(750);
                try { process.RestartCurrentProcess(); }
                catch { await backups.AbortQueuedRestoreAsync("server_restart_failed"); }
            });
            return Results.Json(new RestoreResponse("RestartPending"),
                ServerJsonSerializerContext.Default.RestoreResponse, statusCode: StatusCodes.Status202Accepted);
        }
        catch (BackupException exception) { return Error(exception); }
    }

    private static IResult Error(BackupException exception) =>
        Results.Json(new BackupErrorResponse(exception.Code), ServerJsonSerializerContext.Default.BackupErrorResponse,
            statusCode: exception.Code is "backup_busy" or "restore_already_pending" or "server_operation_in_progress"
                ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);
}

internal sealed record BackupErrorResponse(string Error);
