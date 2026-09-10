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
        var exception = Assert.ThrowsException<InvalidOperationException>(() =>
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

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            manager.EnsureAsync(valid with { Size = 0 }, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            manager.EnsureAsync(valid with { Rid = "wrong-rid" }, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
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
    public async Task ApplyAsync_DuplicateEnabledUdpPort_FailsBeforeCreatingServiceDataOrAdvancingRevision()
    {
        await using var fixture = await ReconcilerFixture.CreateAsync(dataDirectory, new FakeProvider());
        var first = Service("first", "udp");
        var second = Service("second", "udp");
        var result = await fixture.Reconciler.ApplyAsync(Desired(1, [first, second]), fixture.Credentials,
            CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual("invalid_config", result.ErrorCode);
        Assert.IsFalse(Directory.Exists(Path.Combine(dataDirectory, "services")));
        Assert.AreEqual(0L, (await fixture.StateStore.LoadAsync(CancellationToken.None)).AppliedRevision);
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

    private sealed class FakeProvider : IBackendProvider
    {
        public string BackendType => "fake";
        public BackendCapabilities Capabilities => BackendCapabilities.ConfigValidation;

        public ValueTask<BackendValidationResult> ValidateAsync(ServiceDesiredState desiredState,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(desiredState.ConfigJson == "invalid"
                ? new BackendValidationResult(false, [], [], "invalid", "invalid")
                : desiredState.ConfigJson == "udp"
                    ? new BackendValidationResult(true, [], [32100], null, null)
                    : new BackendValidationResult(true, [32100], [], null, null));

        public ValueTask<RenderedBackendConfig> RenderConfigAsync(ServiceDesiredState desiredState,
            CancellationToken cancellationToken)
        {
            var content = Encoding.UTF8.GetBytes($"{{\"service\":\"{desiredState.Name}\"}}");
            return ValueTask.FromResult(new RenderedBackendConfig("config.json", content,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
        }

        public ValueTask<BackendProcessSpec> CreateProcessSpecAsync(BackendInstanceContext instance,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new BackendProcessSpec("/bin/sleep", ["60"], instance.InstanceDirectory,
                new Dictionary<string, string>()));

        public ValueTask<BackendHealthResult> CheckHealthAsync(BackendInstanceContext instance,
            CancellationToken cancellationToken) => ValueTask.FromResult(new BackendHealthResult(true, null, null));

        public ValueTask<BackendTrafficSnapshot?> CollectTrafficAsync(BackendInstanceContext instance,
            CancellationToken cancellationToken) => ValueTask.FromResult<BackendTrafficSnapshot?>(null);
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
            var reconciler = new ServiceReconciler(new BackendProviderRegistry([provider]), binaryManager,
                instanceStore, supervisor, stateStore, TimeProvider.System, NullLogger<ServiceReconciler>.Instance);
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