namespace HyPanel.Agent.Backends.Infrastructure;

using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HyPanel.Shared.Contracts;

public sealed class BackendBinaryManager(
    AgentEnrollmentOptions options,
    AgentCredentialStore credentialStore,
    HttpClient httpClient,
    IHostEnvironment environment,
    ILogger<BackendBinaryManager> logger)
{
    private static readonly SemaphoreSlim DownloadLock = new(1, 1);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    public string CurrentRid => RuntimeInformation.RuntimeIdentifier;

    public async Task<string> EnsureAsync(BackendArtifact artifact, CancellationToken cancellationToken)
    {
        ValidateArtifact(artifact);
        var destinationDirectory = Path.Combine(options.DataDirectory, "backends", artifact.BackendType, artifact.Version, artifact.Rid);
        var destination = EnsureChildPath(destinationDirectory, artifact.FileName);
        Directory.CreateDirectory(destinationDirectory);

        await DownloadLock.WaitAsync(cancellationToken);
        try
        {
            if (await IsExpectedFileAsync(destination, artifact, cancellationToken))
            {
                return destination;
            }

            var credentials = await credentialStore.TryLoadAsync(cancellationToken)
                ?? throw new InvalidOperationException("Agent credentials are required to download a backend binary.");
            var panelUri = ValidatePanelUri(credentials.PanelBaseUrl);
            var assetUri = new Uri(panelUri, $"/api/backend-releases/v1/assets/{Uri.EscapeDataString(artifact.FileName)}");
            var temporary = EnsureChildPath(destinationDirectory, $".{artifact.FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, assetUri);
                request.Headers.Add("X-HyPanel-Agent-Id", credentials.AgentId.ToString("D"));
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credentials.AgentSecret);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(DownloadTimeout);
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                ValidateResponseOrigin(panelUri, response.RequestMessage?.RequestUri);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920];
                long total = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, timeout.Token);
                    if (read == 0) break;
                    total = checked(total + read);
                    if (total > artifact.Size) throw new InvalidDataException("Backend asset exceeded its declared size.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                await output.FlushAsync(timeout.Token);
                if (total != artifact.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(artifact.Sha256)))
                {
                    throw new InvalidDataException("Backend asset size or SHA-256 did not match its declared artifact.");
                }
                SetExecutable(temporary);
                File.Move(temporary, destination, overwrite: true);
                return destination;
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    private async Task<bool> IsExpectedFileAsync(string path, BackendArtifact artifact, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != artifact.Size) return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(artifact.Sha256));
    }

    private void ValidateArtifact(BackendArtifact artifact)
    {
        if (artifact.Rid != CurrentRid || artifact.Size <= 0 || !IsSafeComponent(artifact.BackendType) || !IsSafeComponent(artifact.Version) || !IsSafeBaseName(artifact.FileName) ||
            artifact.Sha256.Length != 64 || artifact.Sha256.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("The backend artifact is invalid for this Agent.");
    }

    private Uri ValidatePanelUri(string panelBaseUrl)
    {
        if (!Uri.TryCreate(panelBaseUrl, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo) ||
            (uri.Scheme != Uri.UriSchemeHttps && !(environment.IsDevelopment() && uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp)))
            throw new InvalidOperationException("The configured Panel URL must use HTTPS (except loopback HTTP in Development).");
        return uri;
    }

    private static void ValidateResponseOrigin(Uri panel, Uri? finalUri)
    {
        if (finalUri is null || !string.Equals(panel.Scheme, finalUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(panel.Host, finalUri.Host, StringComparison.OrdinalIgnoreCase) || panel.Port != finalUri.Port)
            throw new InvalidDataException("Backend download was redirected away from the configured Panel origin.");
    }

    private static bool IsSafeBaseName(string value) => Path.GetFileName(value) == value && IsSafeComponent(value);
    private static bool IsSafeComponent(string value) => !string.IsNullOrWhiteSpace(value) && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
    private static string EnsureChildPath(string parent, string name)
    {
        var root = Path.GetFullPath(parent) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(parent, name));
        return path.StartsWith(root, StringComparison.Ordinal) ? path : throw new InvalidDataException("Path escapes its managed directory.");
    }

    private void SetExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { logger.LogWarning(exception, "Could not mark backend binary executable: {Path}", path); throw; }
    }
}
