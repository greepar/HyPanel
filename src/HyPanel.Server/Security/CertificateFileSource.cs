namespace HyPanel.Server.Security;

/// <summary>
/// Reads a certificate/private-key pair from files on the Panel host (e.g. written by Lucky, certbot or acme.sh).
/// </summary>
internal static class CertificateFileSource
{
    private const long MaximumFileBytes = 262_144;

    public static bool TryRead(string certificatePath, string privateKeyPath, out string certificatePem,
        out string privateKeyPem, out string error)
    {
        certificatePem = privateKeyPem = string.Empty;
        if (!TryReadFile(certificatePath, "证书文件", out certificatePem, out error)) return false;
        return TryReadFile(privateKeyPath, "私钥文件", out privateKeyPem, out error);
    }

    private static bool TryReadFile(string path, string label, out string content, out string error)
    {
        content = error = string.Empty;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { error = $"{label}不存在：{path}"; return false; }
            if (info.Length is 0 or > MaximumFileBytes) { error = $"{label}为空或过大：{path}"; return false; }
            content = File.ReadAllText(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = $"面板服务（用户 hypanel）没有读取{label}的权限：{path}";
            return false;
        }
        catch (IOException exception)
        {
            error = $"无法读取{label}：{exception.Message}";
            return false;
        }
    }
}
