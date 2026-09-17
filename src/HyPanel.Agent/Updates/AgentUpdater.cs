namespace HyPanel.Agent.Updates;

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Versioning;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Reconciliation;

public sealed class AgentUpdater(
    ILogger<AgentUpdater> logger,
    HttpClient httpClient,
    AgentUpdateStateStore stateStore,
    BackendProcessSupervisor processSupervisor,
    ServiceReconciler? reconciler,
    IHostApplicationLifetime applicationLifetime)
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SelfTestTimeout = TimeSpan.FromSeconds(20);
    private const long MaximumExtractedBinarySize = 512L * 1024 * 1024;
    private int _applying;

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        if (state is null) return;
        if (BuildInfo.Version == state.TargetVersion && state.Status is AgentUpdateStatus.Applying or AgentUpdateStatus.RestartPending)
        {
            await stateStore.SaveAsync(state with { Status = AgentUpdateStatus.Verifying, LastError = null }, cancellationToken);
            return;
        }
        if (BuildInfo.Version == state.TargetVersion && state.Status == AgentUpdateStatus.Verifying
            && File.Exists(PreviousPath(state.InstallPath)))
        {
            await RollbackRunningVersionAsync(state, "verification_restart", cancellationToken);
            return;
        }
        if (state.Status is AgentUpdateStatus.Downloading or AgentUpdateStatus.Staged)
        {
            CleanupStaging(state.InstallPath);
            await FailAsync(state, "update_interrupted", cancellationToken);
        }
        else if (state.Status is AgentUpdateStatus.Applying or AgentUpdateStatus.RestartPending)
        {
            await TryRollbackAsync(state, "restart_failed", cancellationToken);
        }
    }

    public async Task RollbackStartupFailureAsync(CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        if (state is null || state.Status != AgentUpdateStatus.Verifying || BuildInfo.Version != state.TargetVersion) return;
        await RollbackRunningVersionAsync(state, "startup_failed", cancellationToken);
    }

    public async Task MarkSyncSucceededAsync(CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        if (state?.Status != AgentUpdateStatus.Verifying) return;
        await stateStore.SaveAsync(state with { Status = AgentUpdateStatus.Succeeded, LastError = null }, cancellationToken);
        TryDelete(PreviousPath(state.InstallPath));
        TryDelete(state.InstallPath + ".update-helper.exe");
    }

    public async Task ApplyOfferAsync(AgentUpdateDescriptor offer, AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _applying, 1) != 0) return;
        try
        {
            var existing = await stateStore.LoadAsync(cancellationToken);
            if (existing?.UpdateId == offer.UpdateId) return;
            if (!TryValidateOffer(offer, out var error))
            {
                logger.LogWarning("Rejected Agent update offer: {Error}", error);
                return;
            }
            if (!ShouldUpdate(BuildInfo.Version, offer.Version)) return;

            var installPath = Path.GetFullPath(Environment.ProcessPath
                ?? throw new InvalidOperationException("The Agent executable path is unavailable."));
            var state = new AgentUpdateLocalState(offer.UpdateId, AgentUpdateStatus.Downloading, offer.Version,
                offer.Rid, offer.FileName, offer.Sha256, offer.Size, DateTimeOffset.UtcNow, BuildInfo.Version, installPath);
            await stateStore.SaveAsync(state, cancellationToken);
            var archivePath = StagingArchivePath(installPath);
            var stagedPath = StagedExecutablePath(installPath);
            try
            {
                logger.LogInformation("Downloading Agent update {Version} for {Rid}.", offer.Version, offer.Rid);
                await DownloadAsync(offer, credentials, archivePath, cancellationToken);
                logger.LogInformation("Extracting verified Agent update {Version}.", offer.Version);
                await ExtractAgentAsync(archivePath, stagedPath, cancellationToken);
                logger.LogInformation("Running self-test for Agent update {Version}.", offer.Version);
                await RunSelfTestAsync(stagedPath, offer, cancellationToken);
                state = state with { Status = AgentUpdateStatus.Staged };
                await stateStore.SaveAsync(state, cancellationToken);
                await ApplyAsync(state, stagedPath, credentials, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Agent update to {Version} failed before restart.", offer.Version);
                CleanupStaging(installPath);
                await FailAsync(state, SafeError(exception), CancellationToken.None);
            }
        }
        finally
        {
            Volatile.Write(ref _applying, 0);
        }
    }

    internal static bool TryValidateOffer(AgentUpdateDescriptor offer, out string error)
    {
        error = string.Empty;
        if (offer.UpdateId == Guid.Empty) error = "update_id_invalid";
        else if (!SemanticVersion.TryParse(offer.Version, out _)) error = "version_invalid";
        else if (offer.Rid != BuildInfo.RuntimeIdentifier) error = "rid_mismatch";
        else if (!IsSafeFileName(offer.FileName) || offer.FileName != ExpectedFileName(offer)) error = "filename_invalid";
        else if (offer.Size <= 0) error = "size_invalid";
        else if (!IsSha256(offer.Sha256)) error = "sha256_invalid";
        return error.Length == 0;
    }

    internal static bool ShouldUpdate(string currentVersion, string targetVersion) =>
        SemanticVersion.TryParse(currentVersion, out var current)
        && SemanticVersion.TryParse(targetVersion, out var target)
        && current.CompareTo(target) < 0;

    internal async Task DownloadAsync(AgentUpdateDescriptor offer, AgentCredentials credentials, string destination,
        CancellationToken cancellationToken)
    {
        var panel = new Uri(credentials.PanelBaseUrl);
        if (!IsAllowedPanelUri(panel)) throw new InvalidDataException("panel_url_invalid");
        var requestUri = new Uri(panel, "/api/releases/v1/assets/" + Uri.EscapeDataString(offer.FileName));
        if (!SameOrigin(panel, requestUri)) throw new InvalidDataException("asset_origin_invalid");
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.Add("X-HyPanel-Agent-Id", credentials.AgentId.ToString("D"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AgentSecret);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { } finalUri || !SameOrigin(panel, finalUri))
            throw new InvalidDataException("asset_redirect_rejected");
        if (response.Content.Headers.ContentLength is { } length && length != offer.Size)
            throw new InvalidDataException("size_mismatch");
        await using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            total += read;
            if (total > offer.Size) throw new InvalidDataException("size_mismatch");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
        if (total != offer.Size) throw new InvalidDataException("size_mismatch");
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("sha256_mismatch");
    }

    internal static async Task ExtractAgentAsync(string archivePath, string stagedPath,
        CancellationToken cancellationToken)
    {
        TryDelete(stagedPath);
        await using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536,
            FileOptions.Asynchronous);
        if (OperatingSystem.IsWindows()) await ExtractZipAsync(archivePath, output, cancellationToken);
        else await ExtractTarAsync(archivePath, output, cancellationToken);
        await output.FlushAsync(cancellationToken);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stagedPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task ExtractTarAsync(string archivePath, Stream output, CancellationToken cancellationToken)
    {
        await using var archive = File.OpenRead(archivePath);
        await using var gzip = new GZipStream(archive, CompressionMode.Decompress);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var binaryCount = 0;
        using var reader = new TarReader(gzip);
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
        {
            var name = NormalizeArchiveName(entry.Name);
            if (name.Length == 0 && entry.EntryType == TarEntryType.Directory) continue;
            if (!seen.Add(name)) throw new InvalidDataException("archive_duplicate_entry");
            if (entry.EntryType != TarEntryType.RegularFile) throw new InvalidDataException("archive_entry_type_invalid");
            if (name == "HyPanel.Agent")
            {
                binaryCount++;
                if (entry.DataStream is null) throw new InvalidDataException("archive_binary_missing");
                await CopyBoundedAsync(entry.DataStream, output, cancellationToken);
            }
            else if (name != "appsettings.json") throw new InvalidDataException("archive_entry_unsupported");
        }
        if (binaryCount != 1) throw new InvalidDataException("archive_binary_count_invalid");
    }

    internal static async Task ExtractZipAsync(string archivePath, Stream output, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var binaryCount = 0;
        foreach (var entry in archive.Entries)
        {
            var name = NormalizeArchiveName(entry.FullName);
            if (!seen.Add(name)) throw new InvalidDataException("archive_duplicate_entry");
            if ((entry.ExternalAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("archive_link_rejected");
            if (name == "HyPanel.Agent.exe")
            {
                binaryCount++;
                await using var source = entry.Open();
                await CopyBoundedAsync(source, output, cancellationToken);
            }
            else if (name != "appsettings.json") throw new InvalidDataException("archive_entry_unsupported");
        }
        if (binaryCount != 1) throw new InvalidDataException("archive_binary_count_invalid");
    }

    private async Task ApplyAsync(AgentUpdateLocalState state, string stagedPath, AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        state = state with { Status = AgentUpdateStatus.Applying };
        await stateStore.SaveAsync(state, cancellationToken);
        logger.LogInformation("Stopping managed backend processes before applying Agent update {Version}.", state.TargetVersion);
        await processSupervisor.StopAllAsync(cancellationToken);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var helperPath = state.InstallPath + ".update-helper.exe";
                File.Copy(state.InstallPath, helperPath, overwrite: true);
                var arguments = $"--update-helper {Environment.ProcessId} {Quote(state.InstallPath)} {Quote(stagedPath)} {state.UpdateId:D}";
                _ = Process.Start(new ProcessStartInfo(helperPath, arguments) { UseShellExecute = false })
                    ?? throw new InvalidOperationException("update_helper_start_failed");
                Environment.ExitCode = 0;
                applicationLifetime.StopApplication();
                return;
            }

            var next = NextPath(state.InstallPath);
            logger.LogInformation("Replacing Agent executable with staged version {Version}.", state.TargetVersion);
            File.Copy(stagedPath, next, overwrite: true);
            File.SetUnixFileMode(next, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ReplaceRunningUnixExecutable(next, state.InstallPath, PreviousPath(state.InstallPath));
            await stateStore.SaveAsync(state with { Status = AgentUpdateStatus.RestartPending }, CancellationToken.None);
            TryDelete(StagingArchivePath(state.InstallPath));
            logger.LogInformation("Re-executing Agent version {Version}.", state.TargetVersion);
            UnixProcess.ReplaceCurrentProcess(state.InstallPath);
        }
        catch
        {
            if (!OperatingSystem.IsWindows() && File.Exists(PreviousPath(state.InstallPath)))
            {
                var failed = state.InstallPath + ".failed";
                TryDelete(failed);
                File.Move(state.InstallPath, failed, overwrite: true);
                File.Move(PreviousPath(state.InstallPath), state.InstallPath, overwrite: true);
            }
            if (reconciler is not null) await reconciler.RestoreAsync(credentials, CancellationToken.None);
            throw;
        }
    }

    private static async Task RunSelfTestAsync(string executable, AgentUpdateDescriptor offer,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(executable, "--self-test")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("self_test_start_failed");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SelfTestTimeout);
        await process.WaitForExitAsync(timeout.Token);
        var output = (await process.StandardOutput.ReadToEndAsync(cancellationToken)).Trim();
        if (process.ExitCode != 0 || output != $"{offer.Version}\t{offer.Rid}")
            throw new InvalidDataException("self_test_failed");
    }

    private async Task TryRollbackAsync(AgentUpdateLocalState state, string error, CancellationToken cancellationToken)
    {
        var previous = PreviousPath(state.InstallPath);
        if (File.Exists(previous) && BuildInfo.Version != state.TargetVersion)
        {
            var failed = state.InstallPath + ".failed";
            TryDelete(failed);
            File.Move(state.InstallPath, failed, overwrite: true);
            File.Move(previous, state.InstallPath, overwrite: true);
        }
        CleanupStaging(state.InstallPath);
        await FailAsync(state, error, cancellationToken);
    }

    private Task FailAsync(AgentUpdateLocalState state, string error, CancellationToken cancellationToken) =>
        stateStore.SaveAsync(state with { Status = AgentUpdateStatus.Failed, LastError = error }, cancellationToken);

    private static string NormalizeArchiveName(string name)
    {
        var normalized = name.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.Contains('/')
            || normalized is "." or ".." || normalized.Contains('\0'))
            throw new InvalidDataException("archive_path_invalid");
        return normalized;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
        && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;
    private static bool IsAllowedPanelUri(Uri uri) => uri.Scheme == Uri.UriSchemeHttps
        || uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    private static string ExpectedFileName(AgentUpdateDescriptor offer) =>
        $"hypanel-agent-{offer.Version}-{offer.Rid}.{(offer.Rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}";
    private static async Task CopyBoundedAsync(Stream source, Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) return;
            total += read;
            if (total > MaximumExtractedBinarySize) throw new InvalidDataException("archive_binary_too_large");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }
    private static bool IsSafeFileName(string value) => value.Length is > 0 and <= 255
        && value == Path.GetFileName(value) && !value.Contains('/') && !value.Contains('\\') && !value.Contains('\0');
    private static bool IsSha256(string value) => value.Length == 64 && value.All(character =>
        character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string SafeError(Exception exception) => exception is InvalidDataException ? exception.Message : "update_failed";
    private static string Quote(string value) => '"' + value.Replace("\"", "\\\"", StringComparison.Ordinal) + '"';
    internal static string PreviousPath(string installPath) => installPath + ".previous";
    internal static string NextPath(string installPath) => installPath + ".next";
    internal static string StagingArchivePath(string installPath) => installPath + ".update-archive";
    internal static string StagedExecutablePath(string installPath) => installPath + ".staged";
    internal static void ReplacePreservingOld(string source, string target, string previous)
    {
        TryDelete(previous);
        File.Replace(source, target, previous, ignoreMetadataErrors: true);
    }
    internal static void ReplaceRunningUnixExecutable(string source, string target, string previous)
    {
        TryDelete(previous);
        File.Copy(target, previous);
        File.Move(source, target, overwrite: true);
    }
    private async Task RollbackRunningVersionAsync(AgentUpdateLocalState state, string error,
        CancellationToken cancellationToken)
    {
        var previous = PreviousPath(state.InstallPath);
        if (!File.Exists(previous)) return;
        var failed = state.InstallPath + ".failed";
        TryDelete(failed);
        File.Move(state.InstallPath, failed, overwrite: true);
        File.Move(previous, state.InstallPath, overwrite: true);
        CleanupStaging(state.InstallPath);
        await FailAsync(state, error, cancellationToken);
        if (!OperatingSystem.IsWindows()) UnixProcess.ReplaceCurrentProcess(state.InstallPath);
    }
    private static void CleanupStaging(string installPath)
    {
        TryDelete(StagingArchivePath(installPath));
        TryDelete(StagedExecutablePath(installPath));
        TryDelete(NextPath(installPath));
        TryDelete(installPath + ".update-helper.exe");
    }
    internal static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
