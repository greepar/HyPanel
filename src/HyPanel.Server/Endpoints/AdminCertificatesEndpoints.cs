namespace HyPanel.Server.Endpoints;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyPanel.Server.Persistence;
using HyPanel.Server.Security;

/// <summary>
/// Kind: <c>Upload</c> (PEM pair), <c>Path</c> (files on the node + domain) or <c>Acme</c> (domain, email, challenge).
/// On replace an empty <see cref="AcmeDnsToken"/> keeps the stored Cloudflare token.
/// </summary>
internal sealed record CertificateUploadRequest(string Name, string? CertificatePem, string? PrivateKeyPem,
    string? Kind = null, string? CertificatePath = null, string? PrivateKeyPath = null, string? Domain = null,
    string? AcmeEmail = null, string? AcmeChallenge = null, string? AcmeDnsToken = null);
internal sealed record CertificateResponse(Guid Id, string Name, string Kind, DateTimeOffset CreatedAtUtc,
    DateTimeOffset? NotBeforeUtc, DateTimeOffset? ExpiresAtUtc, string? Fingerprint, string? Subject, string[] San,
    int UsedBy, string? CertificatePath, string? PrivateKeyPath, string? AcmeEmail, string? AcmeChallenge,
    bool HasAcmeDnsToken, DateTimeOffset? SourceCheckedAtUtc, string? SourceError);
internal sealed record CertificateErrorResponse(string Error);

internal static class AdminCertificatesEndpoints
{
    private const int MaximumPemLength = 262_144;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/admin/v1/certificates", ListAsync);
        endpoints.MapPost("/api/admin/v1/certificates", CreateAsync);
        endpoints.MapPut("/api/admin/v1/certificates/{id:guid}", ReplaceAsync);
        endpoints.MapDelete("/api/admin/v1/certificates/{id:guid}", DeleteAsync);
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
        if (!TryLoadPathFiles(ref body, out var fileError)) return Error(fileError);
        if (!TryCreate(Guid.NewGuid(), body, time.GetUtcNow(), requireToken: true, out var value))
            return Error(body.Kind == "Path" ? "文件中的证书无效、已过期或与私钥不匹配。" : "提交内容无效，请检查表单。");
        if (value.Kind == "Path") value = value with { SourceCheckedAtUtc = time.GetUtcNow() };
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
        var keepsToken = existing.HasAcmeDnsToken && string.IsNullOrWhiteSpace(body.AcmeDnsToken);
        if (!TryLoadPathFiles(ref body, out var fileError)) return Error(fileError);
        if (!TryCreate(id, body, time.GetUtcNow(), requireToken: !keepsToken, out var value))
            return Error(body.Kind == "Path" ? "文件中的证书无效、已过期或与私钥不匹配。" : "提交内容无效，请检查表单。");
        value = value with
        {
            CreatedAtUtc = existing.CreatedAtUtc, UsedBy = existing.UsedBy,
            HasAcmeDnsToken = value.HasAcmeDnsToken || keepsToken && value.AcmeChallenge == "cloudflare"
        };
        return await repository.ReplaceCertificateAsync(value, ct)
            ? Results.Json(Map(value), ServerJsonSerializerContext.Default.CertificateResponse) : Results.Conflict();
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpRequest request, AdminAuthorization auth,
        SqliteServerRepository repository, CancellationToken ct)
    {
        var access = await auth.AuthorizeAsync(request, ct);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        return await repository.DeleteCertificateAsync(id, ct) switch
        {
            null => Results.Conflict(),
            true => Results.NoContent(),
            false => Results.NotFound()
        };
    }

    internal static bool TryCreate(Guid id, CertificateUploadRequest body, DateTimeOffset now,
        out CertificateRecord value) => TryCreate(id, body, now, requireToken: true, out value);

    private static IResult Error(string message) => Results.Json(new CertificateErrorResponse(message),
        ServerJsonSerializerContext.Default.CertificateErrorResponse, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>For Path certificates, reads the PEM pair from the Panel host into the request.</summary>
    private static bool TryLoadPathFiles(ref CertificateUploadRequest body, out string error)
    {
        error = string.Empty;
        if (body.Kind != "Path") return true;
        var certificatePath = body.CertificatePath?.Trim() ?? string.Empty;
        var privateKeyPath = body.PrivateKeyPath?.Trim() ?? string.Empty;
        if (!IsPanelPath(certificatePath) || !IsPanelPath(privateKeyPath) || certificatePath == privateKeyPath)
        {
            error = "请填写两个不同的绝对路径。";
            return false;
        }
        if (!CertificateFileSource.TryRead(certificatePath, privateKeyPath, out var pem, out var key, out error)) return false;
        body = body with { CertificatePath = certificatePath, PrivateKeyPath = privateKeyPath, CertificatePem = pem, PrivateKeyPem = key };
        return true;
    }

    internal static bool TryCreate(Guid id, CertificateUploadRequest body, DateTimeOffset now, bool requireToken,
        out CertificateRecord value)
    {
        value = null!;
        var name = body.Name?.Trim();
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || name.Length > 128) return false;
        return (body.Kind ?? "Upload") switch
        {
            "Upload" => TryCreateUpload(id, name, body, now, out value),
            "Path" => TryCreatePath(id, name, body, now, out value),
            "Acme" => TryCreateAcme(id, name, body, now, requireToken, out value),
            _ => false
        };
    }

    private static bool TryCreateUpload(Guid id, string name, CertificateUploadRequest body, DateTimeOffset now,
        out CertificateRecord value)
    {
        value = null!;
        if (string.IsNullOrWhiteSpace(body.CertificatePem) || string.IsNullOrWhiteSpace(body.PrivateKeyPem) ||
            body.CertificatePem.Length > MaximumPemLength || body.PrivateKeyPem.Length > MaximumPemLength) return false;
        try
        {
            using var cert = X509Certificate2.CreateFromPem(body.CertificatePem, body.PrivateKeyPem);
            if (!cert.HasPrivateKey || now < cert.NotBefore.ToUniversalTime() || now >= cert.NotAfter.ToUniversalTime()) return false;
            var fingerprint = Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
            var san = cert.Extensions.FirstOrDefault(x => x.Oid?.Value == "2.5.29.17")?.Format(false) ?? string.Empty;
            value = new CertificateRecord(id, name, "Upload", body.CertificatePem.Trim() + "\n", now,
                cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime(), fingerprint, cert.Subject, san, 0,
                body.PrivateKeyPem.Trim() + "\n");
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>A Path certificate is an uploaded certificate whose PEM pair is re-read from Panel-host files.</summary>
    private static bool TryCreatePath(Guid id, string name, CertificateUploadRequest body, DateTimeOffset now,
        out CertificateRecord value)
    {
        value = null!;
        if (!IsPanelPath(body.CertificatePath) || !IsPanelPath(body.PrivateKeyPath) ||
            !TryCreateUpload(id, name, body, now, out var loaded)) return false;
        value = loaded with { Kind = "Path", CertificatePath = body.CertificatePath, PrivateKeyPath = body.PrivateKeyPath };
        return true;
    }

    private static bool TryCreateAcme(Guid id, string name, CertificateUploadRequest body, DateTimeOffset now,
        bool requireToken, out CertificateRecord value)
    {
        value = null!;
        var email = body.AcmeEmail?.Trim();
        var challenge = body.AcmeChallenge?.Trim();
        var token = string.IsNullOrWhiteSpace(body.AcmeDnsToken) ? null : body.AcmeDnsToken.Trim();
        if (!TryDomain(body.Domain, out var domain) || email is not { Length: >= 3 and <= 254 } ||
            email.Count(static character => character == '@') != 1 || email.Any(char.IsWhiteSpace) ||
            challenge is not ("http" or "tls" or "cloudflare") ||
            challenge == "cloudflare" && requireToken && token is null ||
            token is { Length: > 256 } || token?.Any(char.IsControl) == true) return false;
        value = new CertificateRecord(id, name, "Acme", null, now, null, null, null, null, $"DNS:{domain}", 0,
            AcmeEmail: email, AcmeChallenge: challenge, AcmeDnsToken: challenge == "cloudflare" ? token : null,
            HasAcmeDnsToken: challenge == "cloudflare" && token is not null);
        return true;
    }

    private static bool IsPanelPath(string? path) => path is { Length: > 1 and <= 1024 } &&
        (path.StartsWith('/') || path.Length > 2 && path[1] == ':') && !path.Any(char.IsControl) && !path.Contains("..");

    internal static bool TryDomain(string? input, out string domain)
    {
        domain = input?.Trim().TrimEnd('.').ToLowerInvariant() ?? string.Empty;
        return domain.Length is > 0 and <= 253 && !domain.Contains('*') &&
               Uri.CheckHostName(domain) == UriHostNameType.Dns && domain.Contains('.');
    }

    private static CertificateResponse Map(CertificateRecord value) => new(value.Id, value.Name, value.Kind,
        value.CreatedAtUtc, value.NotBeforeUtc, value.ExpiresAtUtc, value.Fingerprint, value.Subject,
        string.IsNullOrEmpty(value.San) ? [] : value.San.Split(", ", StringSplitOptions.RemoveEmptyEntries), value.UsedBy,
        value.CertificatePath, value.PrivateKeyPath, value.AcmeEmail, value.AcmeChallenge, value.HasAcmeDnsToken,
        value.SourceCheckedAtUtc, value.SourceError);
}
