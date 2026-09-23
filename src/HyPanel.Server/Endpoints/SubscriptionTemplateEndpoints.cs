namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;

internal sealed record SubscriptionTemplateRequest(string Template);
internal sealed record SubscriptionTemplateResponse(string Template, bool IsCustom, string DefaultTemplate);
internal sealed record SubscriptionTemplateError(string Error);

/// <summary>Admin-editable Mihomo subscription template; DELETE restores the built-in default.</summary>
internal static class SubscriptionTemplateEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/subscription-template", GetAsync);
        endpoints.MapPut("/api/admin/v1/subscription-template", SaveAsync);
        endpoints.MapDelete("/api/admin/v1/subscription-template", ResetAsync);
    }

    private static async Task<IResult> GetAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Json(await repository.GetMihomoTemplateAsync(ct));
    }

    private static async Task<IResult> SaveAsync(SubscriptionTemplateRequest body, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!MihomoTemplate.TryValidate(body.Template, out var error))
            return Results.Json(new SubscriptionTemplateError(error),
                ServerJsonSerializerContext.Default.SubscriptionTemplateError, statusCode: StatusCodes.Status400BadRequest);
        var template = body.Template.Replace("\r\n", "\n", StringComparison.Ordinal);
        await repository.SetMihomoTemplateAsync(template == MihomoTemplate.Default ? null : template, ct);
        return Json(await repository.GetMihomoTemplateAsync(ct));
    }

    private static async Task<IResult> ResetAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        await repository.SetMihomoTemplateAsync(null, ct);
        return Json(null);
    }

    private static IResult Json(string? custom) => Results.Json(
        new SubscriptionTemplateResponse(custom ?? MihomoTemplate.Default, custom is not null, MihomoTemplate.Default),
        ServerJsonSerializerContext.Default.SubscriptionTemplateResponse);
}
