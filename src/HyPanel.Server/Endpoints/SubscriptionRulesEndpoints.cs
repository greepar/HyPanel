using HyPanel.Server.Persistence;
using HyPanel.Server.Subscriptions;

namespace HyPanel.Server.Endpoints;

internal sealed record SubscriptionRulesRequest(string[]? Disabled, string[]? CustomRules);

internal sealed record SubscriptionRuleCategoryResponse(string Id, string Name, string Description, bool Enabled,
    bool Required);

internal sealed record SubscriptionRulesResponse(SubscriptionRuleCategoryResponse[] Categories, string[] CustomRules,
    string[] Policies, bool UsingCustomTemplate);

/// <summary>
/// The rules page: which traffic categories get their own group in the built-in profile, and the admin's own rules,
/// which go before all others. A fully custom template replaces all of it.
/// </summary>
internal static class SubscriptionRulesEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/subscription-rules", GetAsync);
        endpoints.MapPut("/api/admin/v1/subscription-rules", SaveAsync);
    }

    private static async Task<IResult> GetAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await JsonAsync(repository, ct);
    }

    private static async Task<IResult> SaveAsync(SubscriptionRulesRequest body, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var known = BuiltinProfile.Categories.Where(static category => !category.Required)
            .Select(static category => category.Id).ToHashSet(StringComparer.Ordinal);
        string[] disabled = [.. (body.Disabled ?? []).Where(known.Contains).Distinct()];
        // Blank lines and lines starting with # are notes for the admin, not rules.
        string[] custom = [.. (body.CustomRules ?? []).Select(static rule => rule.Trim())
            .Where(static rule => rule.Length > 0 && !rule.StartsWith('#'))];
        if (custom.Length > BuiltinProfile.MaximumCustomRules)
            return Error($"自定义规则最多 {BuiltinProfile.MaximumCustomRules} 条。");
        var rules = new SubscriptionRules(disabled, custom);
        var policies = BuiltinProfile.Policies(rules);
        foreach (var rule in custom)
            if (!BuiltinProfile.TryValidateRule(rule, policies, out var error)) return Error(error);
        await repository.SetSubscriptionRulesAsync(rules, ct);
        return await JsonAsync(repository, ct);
    }

    private static async Task<IResult> JsonAsync(SqliteServerRepository repository, CancellationToken ct)
    {
        var rules = await repository.GetSubscriptionRulesAsync(ct);
        return Results.Json(new SubscriptionRulesResponse(
            [.. BuiltinProfile.Categories.Select(category => new SubscriptionRuleCategoryResponse(category.Id,
                category.Name, category.Description, category.Required || !rules.Disabled.Contains(category.Id),
                category.Required))],
            [.. rules.CustomRules], [.. BuiltinProfile.Policies(rules)],
            await repository.GetMihomoTemplateAsync(ct) is not null),
            ServerJsonSerializerContext.Default.SubscriptionRulesResponse);
    }

    private static IResult Error(string message) =>
        Results.Json(new ApiErrorResponse(message), ServerJsonSerializerContext.Default.ApiErrorResponse,
            statusCode: StatusCodes.Status400BadRequest);
}
