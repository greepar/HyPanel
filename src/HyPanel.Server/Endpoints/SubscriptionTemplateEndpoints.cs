using HyPanel.Server.Persistence;
using HyPanel.Server.Subscriptions;

namespace HyPanel.Server.Endpoints;

internal sealed record SubscriptionTemplateRequest(string Template);
internal sealed record SubscriptionTemplateResponse(string Template, bool IsCustom, string DefaultTemplate);
internal sealed record SubscriptionTemplateError(string Error);

/// <summary>
/// The fully custom Mihomo template, for admins who want to write the whole profile themselves. Without one the
/// built-in profile (shaped by the rules page) is served; DELETE goes back to it.
/// </summary>
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
        return await JsonAsync(repository, ct);
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
        var builtin = BuiltinProfile.BuildTemplate(await repository.GetSubscriptionRulesAsync(ct));
        await repository.SetMihomoTemplateAsync(template == builtin ? null : template, ct);
        return await JsonAsync(repository, ct);
    }

    private static async Task<IResult> ResetAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        await repository.SetMihomoTemplateAsync(null, ct);
        return await JsonAsync(repository, ct);
    }

    private static async Task<IResult> JsonAsync(SqliteServerRepository repository, CancellationToken ct)
    {
        var custom = await repository.GetMihomoTemplateAsync(ct);
        var builtin = BuiltinProfile.BuildTemplate(await repository.GetSubscriptionRulesAsync(ct));
        return Results.Json(new SubscriptionTemplateResponse(custom ?? builtin, custom is not null, builtin),
            ServerJsonSerializerContext.Default.SubscriptionTemplateResponse);
    }
}
