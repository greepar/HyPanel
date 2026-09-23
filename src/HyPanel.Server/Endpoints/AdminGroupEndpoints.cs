namespace HyPanel.Server.Endpoints;

using System.Text;
using HyPanel.Server.Persistence;

/// <summary>User groups: each group lists the services its members may use.</summary>
internal static class AdminGroupEndpoints
{
    private const int MaximumNameLength = 64;
    private const int MaximumServices = 1024;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/groups", ListAsync);
        endpoints.MapPost("/api/admin/v1/groups", CreateAsync);
        endpoints.MapPut("/api/admin/v1/groups/{id:guid}", UpdateAsync);
        endpoints.MapDelete("/api/admin/v1/groups/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return Results.Json((await repository.GetGroupsAsync(ct)).Select(Map).ToArray(),
            ServerJsonSerializerContext.Default.UserGroupResponseArray);
    }

    private static async Task<IResult> CreateAsync(UserGroupRequest body, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct) =>
        await SaveAsync(Guid.NewGuid(), body, create: true, request, authorization, repository, ct);

    private static async Task<IResult> UpdateAsync(Guid id, UserGroupRequest body, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct) =>
        await SaveAsync(id, body, create: false, request, authorization, repository, ct);

    private static async Task<IResult> SaveAsync(Guid id, UserGroupRequest body, bool create, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!TryName(body.Name, out var name) || body.ServiceIds is { Length: > MaximumServices }) return Results.BadRequest();
        if (!await repository.SaveGroupAsync(id, name, body.AutoIncludeNewServices, body.ServiceIds ?? [], create, ct))
            return create ? Results.Conflict() : Results.NotFound();
        var group = (await repository.GetGroupsAsync(ct)).Single(item => item.Id == id);
        return Results.Json(Map(group), ServerJsonSerializerContext.Default.UserGroupResponse,
            statusCode: create ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await repository.DeleteGroupAsync(id, ct) switch
        {
            null => Results.Conflict(),
            true => Results.NoContent(),
            false => Results.NotFound()
        };
    }

    internal static bool TryName(string? input, out string name)
    {
        name = input?.Normalize(NormalizationForm.FormKC).Trim() ?? string.Empty;
        return name.Length is > 0 and <= MaximumNameLength && !name.Any(char.IsControl);
    }

    private static UserGroupResponse Map(UserGroupRecord group) => new(group.Id, group.Name, group.IsDefault,
        group.AutoIncludeNewServices, group.ServiceIds.ToArray(), group.MemberCount);
}
