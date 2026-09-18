using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using HyPanel.Agent;
using HyPanel.Agent.Updates;
using HyPanel.Shared.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using HyPanel.Agent.Backends.Infrastructure;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class AgentUpdaterTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize() => directory = Directory.CreateTempSubdirectory("HyPanel.Agent.Update-").FullName;

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public async Task StateStore_SaveAndLoad_PreservesUpdateLifecycle()
    {
        var store = CreateStore();
        var state = State(AgentUpdateStatus.Applying);
        await store.SaveAsync(state, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(state, loaded);
        Assert.IsTrue(File.Exists(Path.Combine(directory, AgentUpdateStateStore.FileName)));
    }

    [TestMethod]
    public async Task RecoverAsync_WhenDownloadWasInterrupted_MarksFailedAndCleansPartialFiles()
    {
        var store = CreateStore();
        var state = State(AgentUpdateStatus.Downloading);
        await store.SaveAsync(state, CancellationToken.None);
        File.WriteAllText(AgentUpdater.StagingArchivePath(state.InstallPath), "partial");
        var updater = CreateUpdater(store, new StaticHandler(Array.Empty<byte>()));

        await updater.RecoverAsync(CancellationToken.None);

        var recovered = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual(AgentUpdateStatus.Failed, recovered!.Status);
        Assert.AreEqual("update_interrupted", recovered.LastError);
        Assert.IsFalse(File.Exists(AgentUpdater.StagingArchivePath(state.InstallPath)));
    }

    [TestMethod]
    public async Task MarkSyncSucceededAsync_WhenVerifying_MarksSucceededAndCleansUpdateArtifacts()
    {
        var store = CreateStore();
        var state = State(AgentUpdateStatus.Verifying);
        await store.SaveAsync(state, CancellationToken.None);
        File.WriteAllText(AgentUpdater.PreviousPath(state.InstallPath), "old");
        File.WriteAllText(AgentUpdater.StagedExecutablePath(state.InstallPath), "staged");
        File.WriteAllText(AgentUpdater.StagingArchivePath(state.InstallPath), "archive");
        File.WriteAllText(AgentUpdater.RollbackArmedPath(state.InstallPath), "armed");
        var updater = CreateUpdater(store, new StaticHandler(Array.Empty<byte>()));

        await updater.MarkSyncSucceededAsync(CancellationToken.None);

        Assert.AreEqual(AgentUpdateStatus.Succeeded, (await store.LoadAsync(CancellationToken.None))!.Status);
        Assert.IsFalse(File.Exists(AgentUpdater.PreviousPath(state.InstallPath)));
        Assert.IsFalse(File.Exists(AgentUpdater.StagedExecutablePath(state.InstallPath)));
        Assert.IsFalse(File.Exists(AgentUpdater.StagingArchivePath(state.InstallPath)));
        Assert.IsFalse(File.Exists(AgentUpdater.RollbackArmedPath(state.InstallPath)));
    }

    [TestMethod]
    public async Task RecoverAsync_WhenRestartPendingVersionIsRunning_ContinuesAtVerifying()
    {
        var store = CreateStore();
        await store.SaveAsync(State(AgentUpdateStatus.RestartPending), CancellationToken.None);
        var updater = CreateUpdater(store, new StaticHandler(Array.Empty<byte>()));

        await updater.RecoverAsync(CancellationToken.None);

        Assert.AreEqual(AgentUpdateStatus.Verifying, (await store.LoadAsync(CancellationToken.None))!.Status);
    }

    [TestMethod]
    public async Task RecoverAsync_WhenRestartFailed_RestoresPreviousAndCleansStagingFiles()
    {
        var store = CreateStore();
        var state = State(AgentUpdateStatus.Applying) with { TargetVersion = "9.0.0" };
        await store.SaveAsync(state, CancellationToken.None);
        File.WriteAllText(state.InstallPath, "failed");
        File.WriteAllText(AgentUpdater.PreviousPath(state.InstallPath), "previous");
        File.WriteAllText(AgentUpdater.StagingArchivePath(state.InstallPath), "archive");
        File.WriteAllText(AgentUpdater.StagedExecutablePath(state.InstallPath), "staged");
        var updater = CreateUpdater(store, new StaticHandler(Array.Empty<byte>()));

        await updater.RecoverAsync(CancellationToken.None);

        var recovered = await store.LoadAsync(CancellationToken.None);
        Assert.AreEqual(AgentUpdateStatus.Failed, recovered!.Status);
        Assert.AreEqual("restart_failed", recovered.LastError);
        Assert.AreEqual("previous", File.ReadAllText(state.InstallPath));
        Assert.IsFalse(File.Exists(AgentUpdater.StagingArchivePath(state.InstallPath)));
        Assert.IsFalse(File.Exists(AgentUpdater.StagedExecutablePath(state.InstallPath)));
    }

    [DataTestMethod]
    [DataRow("1.0.0", "1.0.0")]
    [DataRow("2.0.0", "1.9.9")]
    [DataRow("1.3.0", "1.3.0-beta.1")]
    public void ShouldUpdate_DoesNotRepeatOrDowngrade(string current, string target)
    {
        Assert.IsFalse(AgentUpdater.ShouldUpdate(current, target));
    }

    [TestMethod]
    public async Task ApplyOfferAsync_WhenUpdateIdWasAlreadySeen_IsIdempotent()
    {
        var store = CreateStore();
        var state = State(AgentUpdateStatus.Failed) with { TargetVersion = "9.0.0" };
        await store.SaveAsync(state, CancellationToken.None);
        var handler = new CountingHandler();
        var updater = CreateUpdater(store, handler);
        var offer = new AgentUpdateDescriptor(state.UpdateId, "9.0.0", BuildInfo.RuntimeIdentifier,
            $"hypanel-agent-9.0.0-{BuildInfo.RuntimeIdentifier}.tar.gz", new string('a', 64), 1);

        await updater.ApplyOfferAsync(offer, Credentials(), CancellationToken.None);

        Assert.AreEqual(0, handler.RequestCount);
        Assert.AreEqual(state, await store.LoadAsync(CancellationToken.None));
    }

    [DataTestMethod]
    [DataRow("../agent.tar.gz", "filename_invalid")]
    [DataRow("agent.tar.gz", "rid_mismatch")]
    public void TryValidateOffer_RejectsUnsafeNameAndWrongRid(string fileName, string expected)
    {
        var rid = expected == "rid_mismatch" ? "win-arm64" : BuildInfo.RuntimeIdentifier;
        var expectedName = $"hypanel-agent-9.0.0-{rid}.{(rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz")}";
        var offer = new AgentUpdateDescriptor(Guid.NewGuid(), "9.0.0", rid,
            expected == "filename_invalid" ? fileName : expectedName, new string('a', 64), 1);

        Assert.IsFalse(AgentUpdater.TryValidateOffer(offer, out var error));
        Assert.AreEqual(expected, error);
    }

    [DataTestMethod]
    [DataRow(true, false, "size_mismatch")]
    [DataRow(false, true, "sha256_mismatch")]
    public async Task DownloadAsync_RejectsSizeAndShaMismatch(bool wrongSize, bool wrongSha, string expected)
    {
        var bytes = "verified bytes"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var offer = new AgentUpdateDescriptor(Guid.NewGuid(), "9.0.0", BuildInfo.RuntimeIdentifier,
            $"hypanel-agent-9.0.0-{BuildInfo.RuntimeIdentifier}.tar.gz",
            wrongSha ? new string('a', 64) : hash, wrongSize ? bytes.Length + 1 : bytes.Length);
        var updater = CreateUpdater(CreateStore(), new StaticHandler(bytes));

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => updater.DownloadAsync(offer,
            Credentials(), Path.Combine(directory, "download"), CancellationToken.None));

        Assert.AreEqual(expected, exception.Message);
    }

    [TestMethod]
    public async Task ExtractAgentAsync_WithValidTar_StagesOnlyAgentBinary()
    {
        var archive = Path.Combine(directory, "agent.tar.gz");
        await CreateTarAsync(archive, ("HyPanel.Agent", "new binary"), ("appsettings.json", "ignored"));
        var staged = Path.Combine(directory, "staged");

        await AgentUpdater.ExtractAgentAsync(archive, staged, CancellationToken.None);

        Assert.AreEqual("new binary", await File.ReadAllTextAsync(staged));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "appsettings.json")));
    }

    [DataTestMethod]
    [DataRow("../HyPanel.Agent", "archive_path_invalid")]
    [DataRow("unexpected", "archive_entry_unsupported")]
    public async Task ExtractAgentAsync_RejectsTraversalAndUnsupportedEntries(string name, string expected)
    {
        var archive = Path.Combine(directory, "bad.tar.gz");
        await CreateTarAsync(archive, (name, "bad"));

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => AgentUpdater.ExtractAgentAsync(
            archive, Path.Combine(directory, "staged"), CancellationToken.None));

        Assert.AreEqual(expected, exception.Message);
    }

    [TestMethod]
    public async Task ExtractAgentAsync_RejectsDuplicateBinaryEntry()
    {
        var archive = Path.Combine(directory, "duplicate.tar.gz");
        await CreateTarAsync(archive, ("HyPanel.Agent", "one"), ("HyPanel.Agent", "two"));

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => AgentUpdater.ExtractAgentAsync(
            archive, Path.Combine(directory, "staged"), CancellationToken.None));

        Assert.AreEqual("archive_duplicate_entry", exception.Message);
    }

    [TestMethod]
    public async Task ExtractAgentAsync_RejectsSymbolicLink()
    {
        var archive = Path.Combine(directory, "link.tar.gz");
        await using (var output = File.Create(archive))
        await using (var gzip = new GZipStream(output, CompressionLevel.NoCompression))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
            writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "HyPanel.Agent") { LinkName = "/bin/sh" });

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => AgentUpdater.ExtractAgentAsync(
            archive, Path.Combine(directory, "staged"), CancellationToken.None));

        Assert.AreEqual("archive_entry_type_invalid", exception.Message);
    }

    [DataTestMethod]
    [DataRow("../HyPanel.Agent.exe", "archive_path_invalid")]
    [DataRow("unexpected.exe", "archive_entry_unsupported")]
    public async Task ExtractZipAsync_RejectsTraversalAndUnsupportedEntries(string name, string expected)
    {
        var archivePath = Path.Combine(directory, "bad.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        await using (var writer = new StreamWriter(archive.CreateEntry(name).Open())) await writer.WriteAsync("bad");
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => AgentUpdater.ExtractZipAsync(
            archivePath, output, CancellationToken.None));

        Assert.AreEqual(expected, exception.Message);
    }

    [TestMethod]
    public async Task ExtractZipAsync_RejectsDuplicateExecutableEntry()
    {
        var archivePath = Path.Combine(directory, "duplicate.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            await using (var first = new StreamWriter(archive.CreateEntry("HyPanel.Agent.exe").Open()))
                await first.WriteAsync("one");
            await using (var second = new StreamWriter(archive.CreateEntry("HyPanel.Agent.exe").Open()))
                await second.WriteAsync("two");
        }
        await using var output = new MemoryStream();

        var exception = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => AgentUpdater.ExtractZipAsync(
            archivePath, output, CancellationToken.None));

        Assert.AreEqual("archive_duplicate_entry", exception.Message);
    }

    [TestMethod]
    public void ReplacePreservingOld_WhenSourceMoveFails_RestoresOldBinary()
    {
        var target = Path.Combine(directory, "HyPanel.Agent");
        var source = Path.Combine(directory, "missing");
        var previous = AgentUpdater.PreviousPath(target);
        File.WriteAllText(target, "old");

        Assert.ThrowsException<FileNotFoundException>(() => AgentUpdater.ReplaceRunningUnixExecutable(source, target, previous));

        Assert.AreEqual("old", File.ReadAllText(target));
        Assert.AreEqual("old", File.ReadAllText(previous));
    }

    [TestMethod]
    public void WindowsHelperPaths_AcceptsOnlyFixedSiblingHelperAndStagingPaths()
    {
        var target = Path.Combine(directory, "HyPanel.Agent.exe");

        Assert.IsTrue(AgentUpdateEntrypoint.IsValidWindowsHelperPaths(target + ".update-helper.exe", target,
            target + ".staged"));
        Assert.IsFalse(AgentUpdateEntrypoint.IsValidWindowsHelperPaths(Path.Combine(directory, "other.exe"), target,
            target + ".staged"));
        Assert.IsFalse(AgentUpdateEntrypoint.IsValidWindowsHelperPaths(target + ".update-helper.exe", target,
            Path.Combine(directory, "other.staged")));
        Assert.IsFalse(AgentUpdateEntrypoint.IsValidWindowsHelperPaths(target + ".update-helper.exe",
            Path.Combine(directory, "Other.exe"), target + ".staged"));
    }

    private AgentUpdateStateStore CreateStore() => new(new AgentEnrollmentOptions(null, null, directory));
    private AgentUpdater CreateUpdater(AgentUpdateStateStore store, HttpMessageHandler handler) => new(
        NullLogger<AgentUpdater>.Instance, new HttpClient(handler), store,
        new BackendProcessSupervisor(NullLogger<BackendProcessSupervisor>.Instance), null, new Lifetime());
    private AgentUpdateLocalState State(AgentUpdateStatus status) => new(Guid.NewGuid(), status, BuildInfo.Version,
        BuildInfo.RuntimeIdentifier, "agent.tar.gz", new string('a', 64), 1, DateTimeOffset.UtcNow, "0.9.0",
        Path.Combine(directory, "HyPanel.Agent"));
    private static AgentCredentials Credentials() => new("https://panel.example", Guid.NewGuid(), "secret",
        Guid.NewGuid(), 8);

    private static async Task CreateTarAsync(string path, params (string Name, string Content)[] entries)
    {
        await using var output = File.Create(path);
        await using var gzip = new GZipStream(output, CompressionLevel.NoCompression);
        using var writer = new TarWriter(gzip, leaveOpen: true);
        foreach (var (name, content) in entries)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(bytes) });
        }
    }

    private sealed class StaticHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(bytes),
        });
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
