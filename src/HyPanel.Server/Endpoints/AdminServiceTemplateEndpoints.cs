namespace HyPanel.Server.Endpoints;

using System.Text;
using HyPanel.Server.Persistence;
using HyPanel.Shared.Contracts;

internal static class AdminServiceTemplateEndpoints
{
    private const int MaximumNameLength = 128;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/service-templates", ListAsync);
        endpoints.MapPost("/api/admin/v1/service-templates", CreateAsync);
        endpoints.MapPut("/api/admin/v1/service-templates/{id:guid}", UpdateAsync);
        endpoints.MapDelete("/api/admin/v1/service-templates/{id:guid}", DeleteAsync);
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/services/from-template/{templateId:guid}",
            InstantiateAsync);
        endpoints.MapPost("/api/admin/v1/services/batch-enabled", SetBatchEnabledAsync);
    }

    private static async Task<IResult> ListAsync(HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var response = (await repository.GetServiceTemplatesAsync(ct)).Select(Map).ToArray();
        return Results.Json(response, ServerJsonSerializerContext.Default.ServiceTemplateResponseArray);
    }

    private static async Task<IResult> CreateAsync(CreateServiceTemplateRequest request, HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, TimeProvider timeProvider,
        CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!TryValidate(request.Name, request.BackendType, request.BackendVersion, request.ConfigSchemaVersion,
                request.ConfigJson, out var name, out var normalizedName, out var backendType, out var version,
                out var config)) return Results.BadRequest();
        var now = timeProvider.GetUtcNow();
        var template = new ServiceTemplateRecord(Guid.NewGuid(), name, normalizedName, backendType, version,
            request.ConfigSchemaVersion, config, now, now);
        return await repository.CreateServiceTemplateAsync(template, ct)
            ? Results.Json(Map(template), ServerJsonSerializerContext.Default.ServiceTemplateResponse,
                statusCode: StatusCodes.Status201Created)
            : Results.Conflict();
    }

    private static async Task<IResult> UpdateAsync(Guid id, UpdateServiceTemplateRequest request,
        HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, TimeProvider timeProvider,
        CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var existing = await repository.GetServiceTemplateAsync(id, ct);
        if (existing is null) return Results.NotFound();
        if (!TryValidate(request.Name, request.BackendType, request.BackendVersion, request.ConfigSchemaVersion,
                request.ConfigJson, out var name, out var normalizedName, out var backendType, out var version,
                out var config)) return Results.BadRequest();
        var updated = existing with
        {
            Name = name, NormalizedName = normalizedName, BackendType = backendType,
            BackendVersion = version, ConfigSchemaVersion = request.ConfigSchemaVersion, ConfigJson = config,
            UpdatedAtUtc = timeProvider.GetUtcNow()
        };
        return await repository.UpdateServiceTemplateAsync(updated, ct) switch
        {
            null => Results.NotFound(), false => Results.Conflict(),
            _ => Results.Json(Map(updated), ServerJsonSerializerContext.Default.ServiceTemplateResponse)
        };
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await repository.DeleteServiceTemplateAsync(id, ct) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> InstantiateAsync(Guid nodeId, Guid templateId,
        CreateServiceFromTemplateRequest request,
        HttpRequest httpRequest, AdminAuthorization authorization, SqliteServerRepository repository,
        CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!AdminNodesEndpoints.TryNormalize(request.Name, MaximumNameLength, out var name))
            return Results.BadRequest();
        var result = await repository.CreateServiceFromTemplateAsync(nodeId, templateId, name, ct);
        return result.Service is null
            ? Results.NotFound()
            : Results.Json(new ServiceMutationResponse(result.Service.Id, result.Revision),
                ServerJsonSerializerContext.Default.ServiceMutationResponse, statusCode: StatusCodes.Status201Created);
    }

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

    internal static bool TryValidate(string? requestedName, string? requestedBackendType, string? requestedVersion,
        int schemaVersion, string? requestedConfig, out string name, out string normalizedName, out string backendType,
        out string version, out string config)
    {
        name = normalizedName = backendType = version = config = string.Empty;
        if (requestedName is null) return false;
        name = requestedName.Normalize(NormalizationForm.FormKC).Trim();
        normalizedName = name.ToLowerInvariant();
        return AdminServicesEndpoints.TryValidateCreate(
            name,
            requestedBackendType,
            requestedVersion,
            schemaVersion,
            requestedConfig,
            out name,
            out backendType,
            out version,
            out config);
    }

    private static ServiceTemplateResponse Map(ServiceTemplateRecord template) => new(template.Id, template.Name,
        template.BackendType, template.BackendVersion, template.ConfigSchemaVersion,
        AdminServicesEndpoints.RedactPasswords(template.ConfigJson), template.CreatedAtUtc, template.UpdatedAtUtc);
}