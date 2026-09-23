namespace HyPanel.Server.Endpoints;

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyPanel.Server.Persistence;
using HyPanel.Server.Releases;
using HyPanel.Shared.Versioning;

internal static class AdminServicesEndpoints
{
    private const int MaximumNameLength = 128;
    private const int MaximumVersionLength = 128;
    private const int MaximumConfigLength = 65536;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/nodes/{nodeId:guid}/services", ListAsync);
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/services", CreateAsync);
        endpoints.MapPut("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}", UpdateAsync);
        endpoints.MapPut("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/enabled", SetEnabledAsync);
        endpoints.MapPut("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/backend-update-policy", SetBackendUpdatePolicyAsync);
        endpoints.MapPost("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/backend-update", RequestBackendUpdateAsync);
        endpoints.MapGet("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/public-endpoint",
            GetPublicEndpointAsync);
        endpoints.MapPut("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/public-endpoint",
            SetPublicEndpointAsync);
        endpoints.MapDelete("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/public-endpoint",
            ClearPublicEndpointAsync);
        endpoints.MapDelete("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(Guid nodeId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, BackendArtifactCatalog releaseCatalog,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(request, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (await repository.GetNodeAsync(nodeId, cancellationToken) is null) return Results.NotFound();
        var services = await repository.GetServicesForNodeAsync(nodeId, cancellationToken);
        var response = services.Select(item => Map(item, releaseCatalog)).ToArray();
        return Results.Json(response, ServerJsonSerializerContext.Default.AdminServiceResponseArray);
    }

    private static async Task<IResult> CreateAsync(Guid nodeId, CreateServiceRequest request, HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!TryValidateCreate(request, out var name, out var backendType, out var version, out var configJson))
            return Results.BadRequest();
        if (!await HasValidCertificateAsync(backendType, configJson, repository, cancellationToken))
            return Results.BadRequest();
        var now = timeProvider.GetUtcNow();
        var result = await repository.CreateServiceAsync(
            new ServiceInstanceRecord(Guid.NewGuid(), nodeId, name, backendType, version, true,
                request.ConfigSchemaVersion, configJson, now, now), cancellationToken);
        if (result.Service is null) return Results.NotFound();
        // A new service is immediately usable: groups that auto-include new services grant it to their members.
        if (Backends.BackendCapabilities.IsMultiUser(backendType))
            await repository.AddServiceToAutoGroupsAsync(result.Service.Id, cancellationToken);
        var revision = (await repository.GetNodeAsync(nodeId, cancellationToken))?.DesiredRevision ?? result.Revision;
        return Results.Json(new ServiceMutationResponse(result.Service.Id, revision),
            ServerJsonSerializerContext.Default.ServiceMutationResponse, statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAsync(Guid nodeId, Guid serviceId, UpdateServiceRequest request,
        HttpRequest httpRequest, AdminAuthorization authorization, SqliteServerRepository repository,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var existing = (await repository.GetServicesForNodeAsync(nodeId, cancellationToken))
            .Select(static item => item.Service)
            .FirstOrDefault(service => service.Id == serviceId);
        if (existing is null) return Results.NotFound();
        if (!TryValidateUpdate(request, out var name, out var version, out var configJson)) return Results.BadRequest();
        if (!TryMergeRedactedSecrets(existing.ConfigJson, configJson, out configJson)) return Results.BadRequest();
        if (!await HasValidCertificateAsync(existing.BackendType, configJson, repository, cancellationToken))
            return Results.BadRequest();
        var now = timeProvider.GetUtcNow();
        var result = await repository.UpdateServiceAsync(
            new ServiceInstanceRecord(serviceId, nodeId, name, existing.BackendType, version, request.Enabled,
                request.ConfigSchemaVersion, configJson, existing.CreatedAtUtc, now), cancellationToken);
        return result.Service is null
            ? Results.NotFound()
            : Results.Json(new ServiceMutationResponse(serviceId, result.Revision),
                ServerJsonSerializerContext.Default.ServiceMutationResponse);
    }

    private static async Task<IResult> DeleteAsync(Guid nodeId, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(request, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var revision = await repository.DeleteServiceAsync(nodeId, serviceId, cancellationToken);
        return revision is null
            ? Results.NotFound()
            : Results.Json(new ServiceMutationResponse(serviceId, revision.Value),
                ServerJsonSerializerContext.Default.ServiceMutationResponse);
    }

    private static async Task<IResult> SetEnabledAsync(Guid nodeId, Guid serviceId, SetServiceEnabledRequest request,
        HttpRequest httpRequest, AdminAuthorization authorization, SqliteServerRepository repository,
        CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var revision = await repository.SetServiceEnabledAsync(nodeId, serviceId, request.Enabled, cancellationToken);
        return revision is null
            ? Results.NotFound()
            : Results.Json(new ServiceMutationResponse(serviceId, revision.Value),
                ServerJsonSerializerContext.Default.ServiceMutationResponse);
    }

    private static async Task<IResult> RequestBackendUpdateAsync(Guid nodeId, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, BackendArtifactCatalog catalog,
        BackendReleaseSyncWorker sync, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var target = await repository.GetBackendUpdateTargetAsync(nodeId, serviceId, ct);
        if (target is null) return Results.NotFound();
        var latest = catalog.GetLatestVersion(target.BackendType);
        if (latest is null) return Results.Conflict();
        if (!SemanticVersion.TryParse(target.DesiredVersion, out var current)
            || !SemanticVersion.TryParse(latest, out var available) || current.CompareTo(available) >= 0)
            return Results.Conflict();
        await sync.EnsureCachedAsync(target.BackendType, latest, target.ReportedRid, ct);
        var revision = await repository.RequestBackendUpdateAsync(nodeId, serviceId, latest, ct);
        return revision is null ? Results.NotFound() : Results.Json(new RequestBackendUpdateResponse(latest, revision.Value),
            ServerJsonSerializerContext.Default.RequestBackendUpdateResponse);
    }

    private static async Task<IResult> SetBackendUpdatePolicyAsync(Guid nodeId, Guid serviceId,
        SetBackendUpdatePolicyRequest body, HttpRequest request, AdminAuthorization authorization,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (body.Policy is not ("Manual" or "Auto")) return Results.BadRequest();
        return await repository.SetBackendUpdatePolicyAsync(nodeId, serviceId, body.Policy, ct)
            ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> GetPublicEndpointAsync(Guid nodeId, Guid serviceId, HttpRequest request,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var endpoint = await repository.GetServicePublicEndpointAsync(nodeId, serviceId, ct);
        if (endpoint is not null)
        {
            return Results.Json(
                new ServicePublicEndpointResponse(endpoint.Host, endpoint.Port, endpoint.TlsServerName,
                    endpoint.UpdatedAtUtc),
                ServerJsonSerializerContext.Default.ServicePublicEndpointResponse);
        }

        var serviceExists = (await repository.GetServicesForNodeAsync(nodeId, ct))
            .Any(item => item.Service.Id == serviceId);
        return serviceExists ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> SetPublicEndpointAsync(Guid nodeId, Guid serviceId,
        ServicePublicEndpointRequest request, HttpRequest httpRequest, AdminAuthorization authorization,
        SqliteServerRepository repository, TimeProvider time, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!TryHost(request.Host, out var host) || request.Port is < 1 or > 65535 ||
            !TryOptionalHost(request.TlsServerName, out var tlsServerName)) return Results.BadRequest();
        var endpoint = new ServicePublicEndpointRecord(serviceId, host, request.Port, tlsServerName, time.GetUtcNow());
        return await repository.SetServicePublicEndpointAsync(nodeId, endpoint, ct)
            ? Results.Json(new ServicePublicEndpointResponse(endpoint.Host, endpoint.Port, endpoint.TlsServerName,
                endpoint.UpdatedAtUtc), ServerJsonSerializerContext.Default.ServicePublicEndpointResponse)
            : Results.NotFound();
    }

    /// <summary>Removes a custom connection address so subscriptions fall back to the automatic one.</summary>
    private static async Task<IResult> ClearPublicEndpointAsync(Guid nodeId, Guid serviceId, HttpRequest httpRequest,
        AdminAuthorization authorization, SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await authorization.AuthorizeAsync(httpRequest, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        await repository.ClearServicePublicEndpointAsync(nodeId, serviceId, ct);
        return Results.NoContent();
    }

    private static bool TryValidateCreate(CreateServiceRequest request, out string name, out string backendType,
        out string version, out string configJson)
        => TryValidateCreate(
            request.Name,
            request.BackendType,
            request.BackendVersion,
            request.ConfigSchemaVersion,
            request.ConfigJson,
            out name,
            out backendType,
            out version,
            out configJson);

    internal static bool TryValidateCreate(
        string? requestedName,
        string? requestedBackendType,
        string? requestedVersion,
        int configSchemaVersion,
        string? requestedConfigJson,
        out string name,
        out string backendType,
        out string version,
        out string configJson)
    {
        name = backendType = version = configJson = string.Empty;
        return configSchemaVersion == 1
               && AdminNodesEndpoints.TryNormalize(requestedName, MaximumNameLength, out name)
               && TryBackendType(requestedBackendType, out backendType)
               && AdminNodesEndpoints.TryNormalize(requestedVersion, MaximumVersionLength, out version)
               && TryNormalizeJson(requestedConfigJson, out configJson);
    }

    internal static bool TryBackendType(string? value, out string backendType)
    {
        backendType = value?.Trim().ToLowerInvariant() ?? string.Empty;
        return backendType is "hysteria2" or "xray" or "mihomo" or "sing-box";
    }

    internal static async Task<bool> HasValidCertificateAsync(string backendType, string configJson,
        SqliteServerRepository repository, CancellationToken ct)
    {
        if (backendType != "hysteria2") return true;
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return document.RootElement.TryGetProperty("certificateId", out var property) &&
                   Guid.TryParse(property.GetString(), out var id) &&
                   await repository.GetCertificateWithKeyAsync(id, ct) is not null;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryValidateUpdate(UpdateServiceRequest request, out string name, out string version,
        out string configJson)
    {
        name = version = configJson = string.Empty;
        return request.ConfigSchemaVersion > 0
               && AdminNodesEndpoints.TryNormalize(request.Name, MaximumNameLength, out name)
               && AdminNodesEndpoints.TryNormalize(request.BackendVersion, MaximumVersionLength, out version)
               && TryNormalizeJson(request.ConfigJson, out configJson);
    }

    internal static bool TryNormalizeJson(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumConfigLength) return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            normalized = document.RootElement.GetRawText();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AdminServiceResponse Map(ServiceInstanceWithRuntimeRecord item, BackendArtifactCatalog catalog)
    {
        var service = item.Service;
        var runtime = item.Runtime;
        return new AdminServiceResponse(service.Id, service.Name, service.BackendType, service.BackendVersion,
            service.Enabled, service.ConfigSchemaVersion, RedactPasswords(service.ConfigJson), service.CreatedAtUtc,
            service.UpdatedAtUtc,
            runtime is null
                ? null
                : new ServiceRuntimeResponse(runtime.Status, runtime.BackendVersion, runtime.AppliedConfigSha256,
                    runtime.TrafficUploadBytes, runtime.TrafficDownloadBytes, runtime.TrafficObservedAtUtc,
                    runtime.ObservedAtUtc, runtime.ErrorCode, runtime.ErrorMessage), service.BackendUpdatePolicy,
            catalog.GetLatestVersion(service.BackendType));
    }

    internal static string RedactPasswords(string configJson)
    {
        var node = JsonNode.Parse(configJson);
        Redact(node);
        return node?.ToJsonString() ?? "null";
    }

    internal static bool TryMergeRedactedSecrets(string existingJson, string submittedJson, out string mergedJson)
    {
        mergedJson = string.Empty;
        try
        {
            var existing = JsonNode.Parse(existingJson);
            var submitted = JsonNode.Parse(submittedJson);
            MergeRedactedSecrets(existing, submitted);
            mergedJson = submitted?.ToJsonString() ?? "null";
            return mergedJson.Length <= MaximumConfigLength;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void MergeRedactedSecrets(JsonNode? existing, JsonNode? submitted)
    {
        if (existing is not JsonObject existingObject || submitted is not JsonObject submittedObject) return;
        foreach (var pair in submittedObject.ToArray())
        {
            if (pair.Value is JsonValue value && value.TryGetValue<string>(out var text) && text == "[REDACTED]" &&
                IsSecretName(pair.Key) && existingObject[pair.Key] is { } existingValue)
                submittedObject[pair.Key] = existingValue.DeepClone();
            else if (existingObject[pair.Key] is { } existingChild)
                MergeRedactedSecrets(existingChild, pair.Value);
        }
    }

    private static bool IsSecretName(string name) =>
        name.EndsWith("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("realityPrivateKey", StringComparison.OrdinalIgnoreCase);

    private static void Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                if (IsSecretName(pair.Key))
                    obj[pair.Key] = "[REDACTED]";
                else Redact(pair.Value);
            }
        }
        else if (node is JsonArray array)
            foreach (var child in array)
                Redact(child);
    }

    private static bool TryOptionalHost(string? input, out string? value)
    {
        if (input is null)
        {
            value = null;
            return true;
        }

        value = string.Empty;
        if (!TryHost(input, out var host) || host.Length > 253) return false;
        value = host;
        return true;
    }

    private static bool TryHost(string? input, out string host)
    {
        host = string.Empty;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 253 || input.Any(char.IsControl) ||
            input.Contains('/') || input.Contains('\\') || input.Contains("://", StringComparison.Ordinal) ||
            input.Any(char.IsWhiteSpace)) return false;
        if (IPAddress.TryParse(input, out _))
        {
            host = input;
            return true;
        }

        var candidate = input.EndsWith(".", StringComparison.Ordinal) ? input[..^1] : input;
        if (candidate.Length is < 1 or > 253) return false;
        foreach (var label in candidate.Split('.'))
        {
            if (label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' ||
                label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return false;
        }

        host = candidate;
        return true;
    }
}
