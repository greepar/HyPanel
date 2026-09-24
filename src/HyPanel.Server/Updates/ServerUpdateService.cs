namespace HyPanel.Server.Updates;

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyPanel.Server.Releases;
using HyPanel.Shared.Versioning;
using HyPanel.Server.Persistence;
using HyPanel.Server.Backup;

internal sealed record ServerUpdateStatus(string CurrentVersion, string? LatestVersion, string DeploymentMode,
    bool UpdateAvailable, string Status, string? Error);
internal sealed record ServerUpdateState(string TargetVersion, string PreviousVersion, string Status,
    string InstallPath, string? Error);

internal sealed partial class ServerUpdateService(IConfiguration configuration, IHttpClientFactory clients,
    IHostApplicationLifetime lifetime, ILogger<ServerUpdateService> logger, SqliteServerRepository? repository = null,
    ServerOperationCoordinator? operations = null) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
    private readonly SemaphoreSlim gate = new(1, 1);
    private GitHubRelease? latest;
    private string? error;
    private string stateStatus = "Idle";
    private string DataDirectory => ServerDataDirectory.Resolve(configuration);
    private string StatePath => Path.Combine(DataDirectory, "server-update-state.json");
    private bool IsDocker => configuration["HyPanel:DeploymentMode"] == "Docker"
                             || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
                             || File.Exists("/.dockerenv");

    public ServerUpdateStatus GetStatus()
    {
        var available = CanUpdate(out var target);
        return new ServerUpdateStatus(ServerBuildInfo.Version, target, IsDocker ? "Docker" : "BareMetal", available,
            stateStatus, error);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RefreshAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { error = "release_check_failed"; logger.LogWarning(exception, "Server release check failed."); }
            await Task.Delay(RefreshInterval, stoppingToken);
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("server-update");
        var mirror = repository is null ? null : (await repository.GetGlobalSettingsAsync(cancellationToken)).GithubMirrorBaseUrl;
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubMirror.Apply(mirror,
            new Uri("https://api.github.com/repos/greepar/HyPanel/releases/latest")));
        request.Headers.UserAgent.ParseAdd("HyPanel/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        latest = await JsonSerializer.DeserializeAsync(stream, BackendReleaseJsonContext.Default.GitHubRelease,
            cancellationToken) ?? throw new InvalidDataException("release_empty");
        _ = NormalizeVersion(latest.TagName);
        error = null;
    }

    public bool CanUpdate(out string? target)
    {
        target = latest is null ? null : NormalizeVersion(latest.TagName);
        return OperatingSystem.IsLinux() && !IsDocker && target is not null
            && SemanticVersion.TryParse(ServerBuildInfo.Version, out var current)
            && SemanticVersion.TryParse(target, out var available) && current.CompareTo(available) < 0;
    }

    public void QueueUpdate()
    {
        if (!CanUpdate(out _)) throw new InvalidOperationException("server_update_unavailable");
        _ = Task.Run(async () => { await Task.Delay(500); await ApplyAsync(CancellationToken.None); });
    }

    internal async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var coordinator = operations ?? new ServerOperationCoordinator();
        if (!coordinator.TryBegin("server-update"))
        {
            error = "server_operation_in_progress";
            stateStatus = "Failed";
            return;
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!CanUpdate(out var version) || latest is null || version is null) return;
            var rid = ServerBuildInfo.RuntimeIdentifier;
            var fileName = $"hypanel-server-{version}-{rid}.tar.gz";
            var asset = latest.Assets.SingleOrDefault(item => item.Name == fileName)
                ?? throw new InvalidOperationException("server_asset_missing");
            if (asset.Size <= 0 || asset.Digest is null || !asset.Digest.StartsWith("sha256:", StringComparison.Ordinal))
                throw new InvalidDataException("server_asset_digest_missing");
            var install = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("process_path_missing"));
            var state = new ServerUpdateState(version, ServerBuildInfo.Version, "Downloading", install, null);
            await SaveStateAsync(state, cancellationToken);
            var archive = install + ".server-update.tar.gz";
            var staged = install + ".server-update.staged";
            await DownloadAsync(asset, archive, cancellationToken);
            await ExtractAsync(archive, staged, cancellationToken);
            await SelfTestAsync(staged, version, rid, cancellationToken);
            await SaveStateAsync(state with { Status = "Applying" }, cancellationToken);
            var previous = install + ".previous";
            File.Delete(previous);
            File.Copy(install, previous);
            File.Move(staged, install, overwrite: true);
            SetExecutable(install);
            await SaveStateAsync(state with { Status = "RestartPending" }, cancellationToken);
            File.Delete(archive);
            try { UnixExec(install); }
            catch
            {
                File.Move(previous, install, overwrite: true);
                throw;
            }
            lifetime.StopApplication();
        }
        catch (Exception exception)
        {
            error = exception is InvalidDataException ? exception.Message : "server_update_failed";
            stateStatus = "Failed";
            logger.LogError(exception, "Server update failed.");
        }
        finally
        {
            gate.Release();
            coordinator.End("server-update");
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath)) return;
        var state = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(StatePath, cancellationToken),
            ServerUpdateJsonContext.Default.ServerUpdateState);
        if (state is null) return;
        if (state.TargetVersion == ServerBuildInfo.Version && state.Status == "Verifying")
        {
            var previous = state.InstallPath + ".previous";
            if (File.Exists(previous))
            {
                File.Move(previous, state.InstallPath, overwrite: true);
                await SaveStateAsync(state with { Status = "Failed", Error = "verification_restart" }, cancellationToken);
                UnixExec(state.InstallPath);
            }
            return;
        }
        if (state.TargetVersion == ServerBuildInfo.Version && state.Status is "Applying" or "RestartPending")
        {
            await SaveStateAsync(state with { Status = "Verifying", Error = null }, cancellationToken);
            await WaitForApplicationStartedAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            await SaveStateAsync(state with { Status = "Succeeded", Error = null }, cancellationToken);
            File.Delete(state.InstallPath + ".previous");
        }
        else stateStatus = state.Status;
    }

    private async Task WaitForApplicationStartedAsync(CancellationToken cancellationToken)
    {
        if (lifetime.ApplicationStarted.IsCancellationRequested) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        using var started = lifetime.ApplicationStarted.Register(() => completion.TrySetResult());
        await completion.Task;
    }

    private async Task DownloadAsync(GitHubReleaseAsset asset, string path, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("server-update");
        var mirror = repository is null ? null : (await repository.GetGlobalSettingsAsync(cancellationToken)).GithubMirrorBaseUrl;
        using var response = await client.GetAsync(GitHubMirror.Apply(mirror, new Uri(asset.BrowserDownloadUrl)), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { Scheme: "https" } final
            || final.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")
            && !string.Equals(final.Host, mirror is null ? null : new Uri(mirror).Host, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("server_asset_redirect_rejected");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long total = 0;
        while (true) { var read = await input.ReadAsync(buffer, cancellationToken); if (read == 0) break; total += read;
            if (total > asset.Size) throw new InvalidDataException("server_asset_size_mismatch"); hash.AppendData(buffer, 0, read); await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken); }
        var digest = asset.Digest ?? throw new InvalidDataException("server_asset_digest_missing");
        if (total != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("server_asset_verification_failed");
    }

    internal static async Task ExtractAsync(string archivePath, string stagedPath, CancellationToken cancellationToken)
    {
        await using var archive = File.OpenRead(archivePath); await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        using var reader = new TarReader(gzip); var found = false;
        while (await reader.GetNextEntryAsync(cancellationToken: cancellationToken) is { } entry)
        {
            var name = entry.Name.TrimStart('.', '/');
            if (name == "hypanel-server" && entry.EntryType == TarEntryType.RegularFile && entry.DataStream is not null)
            { if (found) throw new InvalidDataException("server_archive_ambiguous"); found = true; await using var output = File.Create(stagedPath); await entry.DataStream.CopyToAsync(output, cancellationToken); }
        }
        if (!found) throw new InvalidDataException("server_archive_missing");
        SetExecutable(stagedPath);
    }

    private static async Task SelfTestAsync(string executable, string version, string rid, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "--self-test") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("server_self_test_start_failed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        if (process.ExitCode != 0 || (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim() != $"{version}\t{rid}")
            throw new InvalidDataException("server_self_test_failed");
    }

    private async Task SaveStateAsync(ServerUpdateState state, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(DataDirectory); var temporary = StatePath + ".tmp";
        await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(state,
            ServerUpdateJsonContext.Default.ServerUpdateState), cancellationToken);
        File.Move(temporary, StatePath, overwrite: true); stateStatus = state.Status; error = state.Error;
    }

    private static string NormalizeVersion(string tag) => tag.StartsWith('v') ? tag[1..] : tag;
    private static void SetExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [LibraryImport("libc", EntryPoint = "execv", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int ExecV(string path, nint argv);
    private static unsafe void UnixExec(string path)
    {
        var arg0 = Marshal.StringToCoTaskMemUTF8(path); var argv = stackalloc nint[2]; argv[0] = arg0; argv[1] = 0;
        try { if (ExecV(path, (nint)argv) != 0) throw new InvalidOperationException("server_exec_failed"); }
        finally { Marshal.FreeCoTaskMem(arg0); }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServerUpdateState))]
internal sealed partial class ServerUpdateJsonContext : JsonSerializerContext;
