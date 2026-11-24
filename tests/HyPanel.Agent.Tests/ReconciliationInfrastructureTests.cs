using System.Net;
using System.Security.Cryptography;
using System.Text;
using HyPanel.Agent;
using HyPanel.Agent.Backends;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Reconciliation;
using HyPanel.Shared.Contracts;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class ReconciliationInfrastructureTests
{
    private string dataDirectory = null!;

    [TestInitialize]
    public void Initialize() => dataDirectory =
        Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(dataDirectory))
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void BackendProviderRegistry_DuplicateBackendType_Throws()
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            _ = new BackendProviderRegistry([new FakeProvider(), new FakeProvider()]));

        StringAssert.Contains(exception.Message, "registered more than once");
    }

    [TestMethod]
    public async Task BackendBinaryManager_RejectsInvalidArtifacts_WithoutRequestingThem()
    {
        using var handler = new RecordingHandler(_ =>
            throw new AssertFailedException("Invalid artifacts must not be downloaded."));
        using var client = new HttpClient(handler);
        var manager = await CreateBinaryManagerAsync(client);
        var payload = Encoding.UTF8.GetBytes("backend");
        var valid = Artifact(payload);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            manager.EnsureAsync(valid with { Size = 0 }, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            manager.EnsureAsync(valid with { Rid = "wrong-rid" }, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            manager.EnsureAsync(valid with { FileName = "../backend" }, CancellationToken.None));

        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task BackendBinaryManager_DownloadsAuthenticatedSameOriginArtifact_AndReusesVerifiedFile()
    {
        var payload = Encoding.UTF8.GetBytes("verified backend artifact");
        var expectedAgentId = Guid.NewGuid();
        using var handler = new RecordingHandler(request =>
        {
            Assert.AreEqual("https://panel.example/api/backend-releases/v1/assets/backend.bin",
                request.RequestUri!.AbsoluteUri);
            Assert.AreEqual(expectedAgentId.ToString("D"), request.Headers.GetValues("X-HyPanel-Agent-Id").Single());
            Assert.AreEqual("Bearer", request.Headers.Authorization!.Scheme);
            Assert.AreEqual("agent-secret", request.Headers.Authorization.Parameter);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
                Content = new ByteArrayContent(payload),
            };
        });
        using var client = new HttpClient(handler);
        var manager = await CreateBinaryManagerAsync(client, expectedAgentId);
        var artifact = Artifact(payload);

        var firstPath = await manager.EnsureAsync(artifact, CancellationToken.None);
        var secondPath = await manager.EnsureAsync(artifact, CancellationToken.None);

        Assert.AreEqual(firstPath, secondPath);
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(firstPath));
        Assert.AreEqual(1, handler.RequestCount);
    }

    [TestMethod]
    public void DiskSpace_ImpossibleRequirement_ReportsInsufficientSpace()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(dataDirectory))!;
        var available = new DriveInfo(root).AvailableFreeSpace;

        var exception = Assert.ThrowsExactly<IOException>(() =>
            DiskSpace.Require(dataDirectory, checked(available + 1)));

        Assert.AreEqual("insufficient_space", exception.Message);
    }

    [TestMethod]
    public async Task BackendInstanceStore_TlsAsset_IsSharedProtectedAndPrunedWhenUnused()
    {
        var store = new BackendInstanceStore(new AgentEnrollmentOptions(null, null, dataDirectory));
        var fingerprint = new string('a', 64);
        var asset = new TlsCertificateAsset(Guid.NewGuid(), fingerprint,
            "-----BEGIN CERTIFICATE-----\ncert\n-----END CERTIFICATE-----\n",
            "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----\n");
        var first = Service("tls", "tcp") with { TlsCertificate = asset };
        var second = Service("tls-2", "tcp") with { TlsCertificate = asset };
        var config = new RenderedBackendConfig("config.json", "{}"u8.ToArray(),
            Convert.ToHexString(SHA256.HashData("{}"u8)).ToLowerInvariant());
        foreach (var service in new[] { first, second })
        {
            await store.EnsureTlsAssetAsync(service, CancellationToken.None);
            await store.SaveConfigAsync(service, config, CancellationToken.None);
        }

        var (certificate, key) = store.CertificateFiles(asset);
        Assert.AreEqual(Path.Combine(dataDirectory, "certificates", fingerprint, "cert.pem"), certificate,
            "one copy for every service using the certificate");
        Assert.AreEqual(asset.CertificatePem, await File.ReadAllTextAsync(certificate));
        Assert.AreEqual(asset.PrivateKeyPem, await File.ReadAllTextAsync(key));
        var metadata = await File.ReadAllTextAsync(Path.Combine(store.GetInstanceDirectory(first.ServiceId), "desired.json"));
        Assert.IsFalse(metadata.Contains("PRIVATE KEY", StringComparison.Ordinal));
        Assert.IsFalse(metadata.Contains("CERTIFICATE", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
                            UnixFileMode.OtherRead, File.GetUnixFileMode(certificate));
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(key));
        }

        // An unused certificate and the per-service folder of older Agents go; the shared one stays while used.
        var stale = Path.Combine(dataDirectory, "certificates", new string('b', 64));
        Directory.CreateDirectory(stale);
        var legacy = Path.Combine(store.GetInstanceDirectory(first.ServiceId), "tls", fingerprint);
        Directory.CreateDirectory(legacy);
        await store.PruneCertificatesAsync(CancellationToken.None);
        Assert.IsFalse(Directory.Exists(stale));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(legacy)));
        Assert.IsTrue(File.Exists(certificate));

        store.Delete(first.ServiceId);
        await store.PruneCertificatesAsync(CancellationToken.None);
        Assert.IsTrue(File.Exists(certificate), "the second service still uses it");
        store.Delete(second.ServiceId);
        await store.PruneCertificatesAsync(CancellationToken.None);
        Assert.IsFalse(File.Exists(certificate));
    }

    [TestMethod]
    public async Task ApplyAsync_DuplicateEnabledUdpPort_IsolatesConflictingServiceWithoutAdvancingRevision()
    {
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var first = Service("first", "udp");
        var second = Service("second", "udp");
        var result = await fixture.Reconciler.ApplyAsync(Desired(1, [first, second]), fixture.Credentials,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("port_conflict", result.ErrorCode);
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(first.ServiceId).Status);
        Assert.AreEqual(ServiceRuntimeStatus.Failed,
            fixture.Reconciler.GetRuntimeStates().Single(item => item.ServiceId == second.ServiceId).Status);
        Assert.AreEqual(0L, (await fixture.StateStore.LoadAsync(CancellationToken.None)).AppliedRevision);
    }

    [TestMethod]
    public async Task ApplyAsync_AssignedControlPortInUse_UsesAStableFreePort()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This process assertion uses /bin/sleep and is covered on Unix runners.");
        }

        // Another Agent on the same host already holds the control port the Panel assigned.
        using var occupant = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        occupant.Start();
        var taken = ((System.Net.IPEndPoint)occupant.LocalEndpoint).Port;
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("stats", "tcp") with { ControlPort = taken };

        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(Desired(1, [service]), fixture.Credentials,
            CancellationToken.None)).Succeeded);
        var first = (await fixture.InstanceStore.TryLoadAsync(service.ServiceId, CancellationToken.None))!
            .DesiredState.ControlPort;
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(Desired(2, [service]), fixture.Credentials,
            CancellationToken.None)).Succeeded);
        var second = (await fixture.InstanceStore.TryLoadAsync(service.ServiceId, CancellationToken.None))!
            .DesiredState.ControlPort;

        Assert.IsNotNull(first);
        Assert.AreNotEqual(taken, first);
        Assert.AreEqual(first, second);
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(service.ServiceId).Status);
    }

    [TestMethod]
    public async Task ApplyAsync_SameTcpAndUdpPort_IsAllowed()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This process assertion uses /bin/sleep and is covered on Unix runners.");
        }

        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var tcp = Service("tcp", "tcp");
        var udp = Service("udp", "udp");
        var result =
            await fixture.Reconciler.ApplyAsync(Desired(1, [tcp, udp]), fixture.Credentials, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1L, (await fixture.StateStore.LoadAsync(CancellationToken.None)).AppliedRevision);
    }

    [TestMethod]
    public async Task ApplyAsync_ValidService_SavesStateDesiredAndConfig_AndStartsProcess()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This process assertion uses /bin/sleep and is covered on Unix runners.");
        }

        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("sleep", "tcp");
        var desired = Desired(1, [service]);

        var result = await fixture.Reconciler.ApplyAsync(desired, fixture.Credentials, CancellationToken.None);
        var state = await fixture.StateStore.LoadAsync(CancellationToken.None);
        var metadata = await fixture.InstanceStore.TryLoadAsync(service.ServiceId, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1L, state.AppliedRevision);
        Assert.IsNotNull(state.DesiredState);
        Assert.AreEqual(desired.Revision, state.DesiredState.Revision);
        CollectionAssert.AreEqual(desired.Services.ToArray(), state.DesiredState.Services.ToArray());
        CollectionAssert.AreEqual(desired.BackendArtifacts.ToArray(), state.DesiredState.BackendArtifacts.ToArray());
        Assert.IsNotNull(metadata);
        Assert.AreEqual(service, metadata.DesiredState);
        Assert.IsTrue(File.Exists(Path.Combine(fixture.InstanceStore.GetInstanceDirectory(service.ServiceId),
            metadata.ConfigFileName)));
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(service.ServiceId).Status);
    }

    [TestMethod]
    public async Task ApplyAsync_UnchangedDesiredState_DoesNotRewriteConfig()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses /bin/sleep.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("stable", "tcp");
        var desired = Desired(1, [service]);
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(desired, fixture.Credentials,
            CancellationToken.None)).Succeeded);
        var metadata = await fixture.InstanceStore.TryLoadAsync(service.ServiceId, CancellationToken.None);
        var configPath = Path.Combine(fixture.InstanceStore.GetInstanceDirectory(service.ServiceId),
            metadata!.ConfigFileName);
        var writtenAt = File.GetLastWriteTimeUtc(configPath);

        await Task.Delay(50);
        var repeated = await fixture.Reconciler.ApplyAsync(desired, fixture.Credentials, CancellationToken.None);

        Assert.IsTrue(repeated.Succeeded);
        Assert.AreEqual(writtenAt, File.GetLastWriteTimeUtc(configPath));
    }

    [TestMethod]
    public async Task ApplyAsync_BackendArtifactChangesWithoutConfigChange_RestartsProcess()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses /bin/sleep.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("variant", "tcp");
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(Desired(1, [service]), fixture.Credentials,
            CancellationToken.None)).Succeeded);
        var originalProcessId = fixture.Supervisor.GetStatus(service.ServiceId).ProcessId;
        var replacement = Artifact(ReconcilerFixture.ArtifactPayload) with { FileName = "backend-variant.bin" };

        var result = await fixture.Reconciler.ApplyAsync(new NodeDesiredState(1, [service], [replacement]),
            fixture.Credentials, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreNotEqual(originalProcessId, fixture.Supervisor.GetStatus(service.ServiceId).ProcessId);
        Assert.AreEqual(replacement, (await fixture.StateStore.LoadAsync(CancellationToken.None))
            .DesiredState!.BackendArtifacts.Single());
    }

    [TestMethod]
    public async Task ApplyAsync_FailingArtifactVariant_RestoresPreviousBinary()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses Unix test binaries.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("variant-rollback", "tcp");
        var original = Desired(1, [service]);
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(original, fixture.Credentials,
            CancellationToken.None)).Succeeded);
        var failing = Artifact(ReconcilerFixture.ArtifactPayload) with { FileName = "backend-fail.bin" };

        var result = await fixture.Reconciler.ApplyAsync(new NodeDesiredState(1, [service], [failing]),
            fixture.Credentials, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(service.ServiceId).Status);
        Assert.AreEqual(original.BackendArtifacts.Single(),
            (await fixture.StateStore.LoadAsync(CancellationToken.None)).DesiredState!.BackendArtifacts.Single());
    }

    [TestMethod]
    public async Task RefreshRuntimeStatesAsync_CrashedBackend_RestartsManagedService()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses /bin/sleep.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("recover", "tcp");
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(Desired(1, [service]), fixture.Credentials,
            CancellationToken.None)).Succeeded);
        var original = fixture.Supervisor.GetStatus(service.ServiceId).ProcessId;
        Assert.IsNotNull(original);
        using (var process = System.Diagnostics.Process.GetProcessById(original.Value))
        {
            process.Kill();
            await process.WaitForExitAsync();
        }

        await fixture.Reconciler.RefreshRuntimeStatesAsync(CancellationToken.None);

        var restarted = fixture.Supervisor.GetStatus(service.ServiceId);
        Assert.AreEqual(ServiceRuntimeStatus.Running, restarted.Status);
        Assert.IsNotNull(restarted.ProcessId);
        Assert.AreNotEqual(original.Value, restarted.ProcessId.Value);
    }

    [TestMethod]
    public async Task ApplyAsync_FailingService_DoesNotStopIndependentService()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses Unix test binaries.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var stable = Service("stable", "tcp");
        var failing = Service("failing", "exit") with { ConfigJson = "exit" };

        var result = await fixture.Reconciler.ApplyAsync(Desired(1, [stable, failing]), fixture.Credentials,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(stable.ServiceId).Status);
        Assert.AreEqual(ServiceRuntimeStatus.Failed,
            fixture.Reconciler.GetRuntimeStates().Single(item => item.ServiceId == failing.ServiceId).Status);

        var repeated = await fixture.Reconciler.ApplyAsync(Desired(1, [stable, failing]), fixture.Credentials,
            CancellationToken.None);
        var failedRuntime = fixture.Reconciler.GetRuntimeStates().Single(item => item.ServiceId == failing.ServiceId);

        Assert.IsFalse(repeated.Succeeded);
        Assert.AreEqual(ServiceRuntimeStatus.Backoff, failedRuntime.Status);
        Assert.AreEqual("backend_restart_backoff", failedRuntime.ErrorCode);
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(stable.ServiceId).Status);
    }

    [TestMethod]
    public async Task ApplyAsync_TakenPort_KeepsItsReasonWhileBackingOff()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses Unix test binaries.");
        // Something else (nginx, say) holds the service's port.
        using var occupant = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 32100);
        occupant.Start();
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("blocked", "tcp");

        Assert.IsFalse((await fixture.Reconciler.ApplyAsync(Desired(1, [service]), fixture.Credentials,
            CancellationToken.None)).Succeeded);
        Assert.IsFalse((await fixture.Reconciler.ApplyAsync(Desired(1, [service]), fixture.Credentials,
            CancellationToken.None)).Succeeded);
        var runtime = fixture.Reconciler.GetRuntimeStates().Single(item => item.ServiceId == service.ServiceId);

        Assert.AreEqual(ServiceRuntimeStatus.Backoff, runtime.Status);
        Assert.AreEqual("port_in_use", runtime.ErrorCode, "waiting to retry does not hide why it failed");
        StringAssert.Contains(runtime.ErrorMessage, "32100");
    }

    [TestMethod]
    public async Task ApplyAsync_InvalidSecondRevision_PreservesPreviousRevisionAndDesiredState()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This process assertion uses /bin/sleep and is covered on Unix runners.");
        }

        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var initialService = Service("stable", "tcp");
        var initial = Desired(1, [initialService]);
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(initial, fixture.Credentials, CancellationToken.None))
            .Succeeded);

        var invalid = Desired(2, [initialService with { ConfigJson = "invalid" }]);
        var result = await fixture.Reconciler.ApplyAsync(invalid, fixture.Credentials, CancellationToken.None);
        var saved = await fixture.StateStore.LoadAsync(CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("invalid_config", result.ErrorCode);
        Assert.AreEqual(1L, saved.AppliedRevision);
        Assert.IsNotNull(saved.DesiredState);
        Assert.AreEqual(initial.Revision, saved.DesiredState.Revision);
        CollectionAssert.AreEqual(initial.Services.ToArray(), saved.DesiredState.Services.ToArray());
        CollectionAssert.AreEqual(initial.BackendArtifacts.ToArray(), saved.DesiredState.BackendArtifacts.ToArray());
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(initialService.ServiceId).Status);
    }

    [TestMethod]
    public async Task ApplyAsync_InvalidService_DoesNotBlockIndependentValidService()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses /bin/sleep.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var invalid = Service("invalid", "invalid");
        var valid = Service("valid", "udp");

        var result = await fixture.Reconciler.ApplyAsync(Desired(1, [invalid, valid]), fixture.Credentials,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0L, (await fixture.StateStore.LoadAsync(CancellationToken.None)).AppliedRevision);
        Assert.AreEqual(ServiceRuntimeStatus.Failed,
            fixture.Reconciler.GetRuntimeStates().Single(item => item.ServiceId == invalid.ServiceId).Status);
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(valid.ServiceId).Status);
        var recovery = await fixture.StateStore.LoadAsync(CancellationToken.None);
        Assert.IsNotNull(recovery.DesiredState);
        Assert.AreEqual(valid.ServiceId, recovery.DesiredState.Services.Single().ServiceId);
    }

    [TestMethod]
    public async Task ApplyAsync_UpdatedBackendExitsDuringHealthWindow_RestoresLastKnownGoodService()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses Unix test binaries.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("rollback", "tcp");
        var initial = Desired(1, [service]);
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(initial, fixture.Credentials, CancellationToken.None)).Succeeded);

        var updatedService = service with { BackendVersion = "2.0.0", ConfigJson = "exit" };
        var failed = new NodeDesiredState(2, [updatedService],
        [
            Artifact(ReconcilerFixture.ArtifactPayload),
            Artifact(ReconcilerFixture.ArtifactPayload) with { Version = "2.0.0" }
        ]);
        var result = await fixture.Reconciler.ApplyAsync(failed, fixture.Credentials, CancellationToken.None);
        var saved = await fixture.StateStore.LoadAsync(CancellationToken.None);
        var metadata = await fixture.InstanceStore.TryLoadAsync(service.ServiceId, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(1L, saved.AppliedRevision);
        Assert.AreEqual("tcp", metadata!.DesiredState.ConfigJson);
        Assert.AreEqual(ServiceRuntimeStatus.Running, fixture.Supervisor.GetStatus(service.ServiceId).Status);
        var runtime = fixture.Reconciler.GetRuntimeStates().Single(item => item.ServiceId == service.ServiceId);
        Assert.AreEqual("1.0.0", runtime.BackendVersion);
        Assert.AreEqual("backend_update_rolled_back", runtime.ErrorCode);
    }

    [TestMethod]
    public async Task ApplyAsync_ConfigTheProgramRejects_LeavesTheRunningInstanceUntouched()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses Unix test binaries.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("switch", "tcp");
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(Desired(1, [service]), fixture.Credentials, CancellationToken.None)).Succeeded);
        var processId = fixture.Supervisor.GetStatus(service.ServiceId).ProcessId;

        var result = await fixture.Reconciler.ApplyAsync(Desired(2, [service with { ConfigJson = "rejected" }]),
            fixture.Credentials, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(processId, fixture.Supervisor.GetStatus(service.ServiceId).ProcessId, "never stopped");
        Assert.AreEqual("tcp", (await fixture.InstanceStore.TryLoadAsync(service.ServiceId, CancellationToken.None))!
            .DesiredState.ConfigJson);
        var runtime = fixture.Reconciler.GetRuntimeStates().Single(item => item.ServiceId == service.ServiceId);
        Assert.AreEqual("config_rejected", runtime.ErrorCode);
        StringAssert.Contains(runtime.ErrorMessage, "bad key");
        Assert.AreEqual(0, Directory.GetFiles(fixture.InstanceStore.GetInstanceDirectory(service.ServiceId), ".candidate-*").Length);
    }

    [TestMethod]
    public async Task ApplyAsync_SuccessfulUpdate_DropsTheRollbackCopy()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("This process assertion uses Unix test binaries.");
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("update", "tcp");
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(Desired(1, [service]), fixture.Credentials, CancellationToken.None)).Succeeded);

        var updated = new NodeDesiredState(2, [service with { BackendVersion = "2.0.0" }],
            [Artifact(ReconcilerFixture.ArtifactPayload) with { Version = "2.0.0" }]);
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(updated, fixture.Credentials, CancellationToken.None)).Succeeded);

        var directory = fixture.InstanceStore.GetInstanceDirectory(service.ServiceId);
        Assert.IsFalse(Directory.Exists(Path.Combine(directory, "last-known-good")),
            "the new version runs, so the copy kept for rolling back is removed");
        Assert.AreEqual(1, Directory.GetFiles(directory, "*-config*").Length, "only the running config is left");
    }

    [TestMethod]
    public async Task ApplyAsync_RemovedService_StopsProcessAndDeletesManagedDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This process assertion uses /bin/sleep and is covered on Unix runners.");
        }

        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var service = Service("removed", "tcp");
        Assert.IsTrue((await fixture.Reconciler.ApplyAsync(Desired(1, [service]), fixture.Credentials,
            CancellationToken.None)).Succeeded);
        var directory = fixture.InstanceStore.GetInstanceDirectory(service.ServiceId);
        Assert.IsTrue(Directory.Exists(directory));

        var result = await fixture.Reconciler.ApplyAsync(Desired(2, []), fixture.Credentials,
            CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(2L, (await fixture.StateStore.LoadAsync(CancellationToken.None)).AppliedRevision);
        Assert.AreEqual(ServiceRuntimeStatus.Stopped, fixture.Supervisor.GetStatus(service.ServiceId).Status);
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public async Task PruneUnusedAsync_KeepsOnlyReferencedBinaries()
    {
        using var client = new HttpClient();
        var manager = await CreateBinaryManagerAsync(client);
        var root = Path.Combine(dataDirectory, "backends");
        string Binary(string name)
        {
            var directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            File.WriteAllText(path, name);
            return path;
        }
        var standard = Binary("hysteria2-2.12.3-linux-x64");
        var avx = Binary("hysteria2-2.12.3-linux-x64-avx");
        var older = Binary("hysteria2-2.12.2-linux-x64");
        // The layout of Agents before 0.5.38, one copy per backend type: removed as well.
        var legacyDirectory = Path.Combine(root, "xray-hysteria2", "26.11.9", "linux-x64");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "xray-26.11.9-linux-x64"), "legacy");

        await manager.PruneUnusedAsync(
            [new BackendArtifact("hysteria2", "2.12.3", "linux-x64", "hysteria2-2.12.3-linux-x64-avx", new string('0', 64), 1, true)],
            CancellationToken.None);

        Assert.IsTrue(File.Exists(avx));
        Assert.IsFalse(File.Exists(standard));
        Assert.IsFalse(File.Exists(older));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(older)));
        Assert.IsFalse(Directory.Exists(Path.Combine(root, "xray-hysteria2")));
    }

    [TestMethod]
    public async Task EnsureAsync_BackendsOfOneEngineShareOneCopyAndReuseALegacyOne()
    {
        // No Panel is reachable: every binary must come from what is already on disk.
        using var client = new HttpClient();
        var manager = await CreateBinaryManagerAsync(client);
        var payload = "xray-binary"u8.ToArray();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        var legacy = Path.Combine(dataDirectory, "backends", "xray", "26.11.9", manager.CurrentRid);
        Directory.CreateDirectory(legacy);
        File.WriteAllBytes(Path.Combine(legacy, "xray-26.11.9-test"), payload);
        BackendArtifact For(string backendType) =>
            new(backendType, "26.11.9", manager.CurrentRid, "xray-26.11.9-test", sha, payload.Length);

        var reality = await manager.EnsureAsync(For("xray"), CancellationToken.None);
        var hysteria = await manager.EnsureAsync(For("xray-hysteria2"), CancellationToken.None);

        Assert.AreEqual(reality, hysteria, "one engine, one file");
        Assert.AreEqual(Path.Combine(dataDirectory, "backends", "xray-26.11.9-test", "xray-26.11.9-test"), reality);
        CollectionAssert.AreEqual(payload, File.ReadAllBytes(reality));
    }

    private async Task<BackendBinaryManager> CreateBinaryManagerAsync(HttpClient client, Guid? agentId = null)
    {
        var options = new AgentEnrollmentOptions(null, null, dataDirectory);
        var credentialStore = new AgentCredentialStore(options, NullLogger<AgentCredentialStore>.Instance);
        await credentialStore.SaveAsync(
            new AgentCredentials("https://panel.example", agentId ?? Guid.NewGuid(), "agent-secret", Guid.NewGuid(),
                30), CancellationToken.None);
        return new BackendBinaryManager(options, credentialStore, client, new TestHostEnvironment(),
            NullLogger<BackendBinaryManager>.Instance);
    }

    private static BackendArtifact Artifact(byte[] payload) => new("fake", "1.0.0",
        System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "backend.bin",
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), payload.Length);

    private static ServiceDesiredState Service(string name, string protocol) =>
        new(Guid.NewGuid(), name, "fake", "1.0.0", true, 1, protocol);

    private static NodeDesiredState Desired(long revision, IReadOnlyList<ServiceDesiredState> services) =>
        new(revision, services, [Artifact(ReconcilerFixture.ArtifactPayload)]);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(responder(request));
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "HyPanel.Agent.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeProvider : IBackendProvider, IConfigTestingBackend
    {
        /// <summary>Configs containing "rejected" fail the program's own test, like <c>xray run -test</c> would.</summary>
        public ValueTask<string?> TestConfigAsync(BackendInstanceContext instance, CancellationToken cancellationToken) =>
            ValueTask.FromResult(File.ReadAllText(instance.ConfigPath).Contains("rejected", StringComparison.Ordinal)
                ? "内核拒绝了新配置：bad key"
                : null);

        public string BackendType => "fake";
        public BackendCapabilities Capabilities => BackendCapabilities.ConfigValidation;

        public ValueTask<BackendValidationResult> ValidateAsync(ServiceDesiredState desiredState,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(desiredState.ConfigJson == "invalid"
                ? new BackendValidationResult(false, [], [], "invalid", "invalid")
                : desiredState.ConfigJson is "udp" or "exit"
                    ? new BackendValidationResult(true, [], [32100], null, null)
                    : new BackendValidationResult(true, [32100], [], null, null));

        public ValueTask<RenderedBackendConfig> RenderConfigAsync(ServiceDesiredState desiredState,
            CancellationToken cancellationToken)
        {
            var content = Encoding.UTF8.GetBytes($"{{\"service\":\"{desiredState.Name}\",\"config\":\"{desiredState.ConfigJson}\"}}");
            return ValueTask.FromResult(new RenderedBackendConfig("config.json", content,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
        }

        public ValueTask<BackendProcessSpec> CreateProcessSpecAsync(BackendInstanceContext instance,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(instance.DesiredState.ConfigJson == "exit" ||
                                 instance.BinaryPath.EndsWith("backend-fail.bin", StringComparison.Ordinal)
                ? new BackendProcessSpec("/usr/bin/false", [], instance.InstanceDirectory,
                    new Dictionary<string, string>())
                : new BackendProcessSpec("/bin/sleep", ["60"], instance.InstanceDirectory,
                    new Dictionary<string, string>()));

        public ValueTask<BackendHealthResult> CheckHealthAsync(BackendInstanceContext instance,
            CancellationToken cancellationToken) => ValueTask.FromResult(new BackendHealthResult(true, null, null));

        public ValueTask<IReadOnlyList<BackendUserTraffic>> CollectUserTrafficAsync(BackendInstanceContext instance,
            CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<BackendUserTraffic>>([]);
    }

    private sealed class ReconcilerFixture : IAsyncDisposable
    {
        internal static readonly byte[] ArtifactPayload = Encoding.UTF8.GetBytes("fake provider artifact");
        private readonly HttpClient client;
        private readonly RecordingHandler handler;
        public AgentCredentials Credentials { get; }
        public AgentStateStore StateStore { get; }
        public BackendInstanceStore InstanceStore { get; }
        public BackendProcessSupervisor Supervisor { get; }
        public ServiceReconciler Reconciler { get; }

        private ReconcilerFixture(HttpClient client, RecordingHandler handler, AgentCredentials credentials,
            AgentStateStore stateStore, BackendInstanceStore instanceStore, BackendProcessSupervisor supervisor,
            ServiceReconciler reconciler)
        {
            this.client = client;
            this.handler = handler;
            Credentials = credentials;
            StateStore = stateStore;
            InstanceStore = instanceStore;
            Supervisor = supervisor;
            Reconciler = reconciler;
        }

        public static async Task<ReconcilerFixture> CreateAsync(string directory, IBackendProvider provider)
        {
            var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, request.RequestUri),
                Content = new ByteArrayContent(ArtifactPayload),
            });
            var client = new HttpClient(handler);
            var options = new AgentEnrollmentOptions(null, null, directory);
            var credentials = new AgentCredentials("https://panel.example", Guid.NewGuid(), "agent-secret",
                Guid.NewGuid(), 30);
            var credentialStore = new AgentCredentialStore(options, NullLogger<AgentCredentialStore>.Instance);
            await credentialStore.SaveAsync(credentials, CancellationToken.None);
            var binaryManager = new BackendBinaryManager(options, credentialStore, client, new TestHostEnvironment(),
                NullLogger<BackendBinaryManager>.Instance);
            var stateStore = new AgentStateStore(options);
            var instanceStore = new BackendInstanceStore(options);
            var supervisor = new BackendProcessSupervisor(NullLogger<BackendProcessSupervisor>.Instance);
            var usageStore = new AgentUsageStateStore(options);
            await usageStore.LoadAsync(CancellationToken.None);
            var reconciler = new ServiceReconciler(new BackendProviderRegistry([provider]), binaryManager,
                instanceStore, supervisor, stateStore, usageStore, TimeProvider.System, NullLogger<ServiceReconciler>.Instance);
            return new ReconcilerFixture(client, handler, credentials, stateStore, instanceStore, supervisor,
                reconciler);
        }

        public async ValueTask DisposeAsync()
        {
            await Supervisor.DisposeAsync();
            client.Dispose();
            handler.Dispose();
        }
    }
}
