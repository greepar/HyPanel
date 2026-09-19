namespace HyPanel.Server.Endpoints;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyPanel.Server.Persistence;

internal sealed record CertificateUploadRequest(string Name, string CertificatePem, string PrivateKeyPem);
internal sealed record CertificateResponse(Guid Id, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset NotBeforeUtc,
    DateTimeOffset ExpiresAtUtc, string Fingerprint, string Subject, string[] San, int UsedBy);

internal static class AdminCertificatesEndpoints
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/certificates", ListAsync);
        endpoints.MapPost("/api/admin/v1/certificates", CreateAsync);
        endpoints.MapPut("/api/admin/v1/certificates/{id:guid}", ReplaceAsync);
    }

    private static async Task<IResult> ListAsync(HttpRequest request, AdminAuthorization auth,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await auth.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var values = (await repository.GetCertificatesAsync(ct)).Select(Map).ToArray();
        return Results.Json(values, ServerJsonSerializerContext.Default.CertificateResponseArray);
    }

    private static async Task<IResult> CreateAsync(CertificateUploadRequest body, HttpRequest request,
        AdminAuthorization auth, SqliteServerRepository repository, TimeProvider time, CancellationToken ct)
    {
        var access = await auth.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!TryCreate(Guid.NewGuid(), body, time.GetUtcNow(), out var value)) return Results.BadRequest();
        return await repository.CreateCertificateAsync(value, ct)
            ? Results.Json(Map(value), ServerJsonSerializerContext.Default.CertificateResponse,
                statusCode: StatusCodes.Status201Created) : Results.Conflict();
    }

    private static async Task<IResult> ReplaceAsync(Guid id, CertificateUploadRequest body, HttpRequest request,
        AdminAuthorization auth, SqliteServerRepository repository, TimeProvider time, CancellationToken ct)
    {
        var access = await auth.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var existing = (await repository.GetCertificatesAsync(ct)).SingleOrDefault(item => item.Id == id);
        if (existing is null) return Results.NotFound();
        if (!TryCreate(id, body, time.GetUtcNow(), out var value)) return Results.BadRequest();
        value = value with { CreatedAtUtc = existing.CreatedAtUtc, UsedBy = existing.UsedBy };
        return await repository.ReplaceCertificateAsync(value, ct)
            ? Results.Json(Map(value), ServerJsonSerializerContext.Default.CertificateResponse) : Results.Conflict();
    }

    internal static bool TryCreate(Guid id, CertificateUploadRequest body, DateTimeOffset now,
        out CertificateRecord value)
    {
        value = null!;
        var name = body.Name?.Trim();
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            string.IsNullOrWhiteSpace(body.CertificatePem) || string.IsNullOrWhiteSpace(body.PrivateKeyPem) ||
            body.CertificatePem.Length > 262144 || body.PrivateKeyPem.Length > 262144) return false;
        try
        {
            using var cert = X509Certificate2.CreateFromPem(body.CertificatePem, body.PrivateKeyPem);
            if (!cert.HasPrivateKey || now < cert.NotBefore.ToUniversalTime() || now >= cert.NotAfter.ToUniversalTime()) return false;
            var fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
            var san = cert.Extensions.FirstOrDefault(x => x.Oid?.Value == "2.5.29.17")?.Format(false) ?? string.Empty;
            value = new CertificateRecord(id, name, body.CertificatePem.Trim() + "\n", now,
                cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), fingerprint, cert.Subject, san, 0,
                body.PrivateKeyPem.Trim() + "\n");
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    private static CertificateResponse Map(CertificateRecord value) => new(value.Id, value.Name, value.CreatedAtUtc,
        value.NotBeforeUtc, value.ExpiresAtUtc, value.Fingerprint, value.Subject,
        string.IsNullOrEmpty(value.San) ? [] : value.San.Split(", ", StringSplitOptions.RemoveEmptyEntries), value.UsedBy);
}
