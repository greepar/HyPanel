namespace HyPanel.Agent.Reconciliation;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using HyPanel.Agent.Backends;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Shared.Contracts;

public sealed record ApplyResult(bool Succeeded, string? ErrorCode, string? ErrorMessage);

public sealed class ServiceReconciler(
    BackendProviderRegistry providers,
    BackendBinaryManager binaryManager,
    BackendInstanceStore instanceStore,
    BackendProcessSupervisor processSupervisor,
    AgentStateStore stateStore,
    TimeProvider timeProvider,
    ILogger<ServiceReconciler> logger)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<Guid, ServiceRuntimeState> runtimeStates = new();
    private readonly SemaphoreSlim applyGate = new(1, 1);

    public IReadOnlyList<ServiceRuntimeState> GetRuntimeStates() =>
        runtimeStates.Values.OrderBy(state => state.ServiceId).ToArray();

    public async Task<ApplyResult> ApplyAsync(NodeDesiredState desiredState, AgentCredentials credentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredState);
        ArgumentNullException.ThrowIfNull(credentials);

        await applyGate.WaitAsync(cancellationToken);
        try
        {
            PreflightResult preflight;
            try
            {
                preflight = await PreflightAsync(desiredState, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                logger.LogWarning("Desired state preflight failed.");
                return new ApplyResult(false, "invalid_desired_state", SafeMessage("invalid_desired_state"));
            }

            if (!preflight.Succeeded)
            {
                SetFailed(preflight.ServiceId, preflight.ErrorCode);
                return new ApplyResult(false, preflight.ErrorCode, SafeMessage(preflight.ErrorCode));
            }

            var changed = new List<Guid>();
            try
            {
                foreach (var plan in preflight.Plans)
                {
                    await ApplyServiceAsync(plan, changed, cancellationToken);
                }

                var previous = await stateStore.LoadAsync(cancellationToken);
                if (previous.DesiredState is not null)
                {
                    var desiredIds = preflight.Plans.Select(plan => plan.Desired.ServiceId).ToHashSet();
                    foreach (var removed in previous.DesiredState.Services.Where(service =>
                                 !desiredIds.Contains(service.ServiceId)))
                    {
                        var stopped =
                            await processSupervisor.StopAsync(removed.ServiceId, StopTimeout, cancellationToken);
                        if (stopped.Status == ServiceRuntimeStatus.Failed)
                        {
                            throw new InvalidOperationException("A removed backend process could not be stopped.");
                        }

                        instanceStore.Delete(removed.ServiceId);
                        runtimeStates[removed.ServiceId] = State(removed.ServiceId, ServiceRuntimeStatus.Stopped,
                            removed.BackendVersion, null, null, null);
                    }
                }

                await stateStore.SaveAsync(new AgentLocalState(desiredState.Revision, desiredState), cancellationToken);
                return new ApplyResult(true, null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                logger.LogWarning("Service reconciliation failed with error code {ErrorCode}.", "apply_failed");
                await RollbackAsync(changed, desiredState, cancellationToken);
                var failedService = changed.Count == 0 ? Guid.Empty : changed[^1];
                SetFailed(failedService, "apply_failed");
                return new ApplyResult(false, "apply_failed", SafeMessage("apply_failed"));
            }
        }
        finally
        {
            applyGate.Release();
        }
    }

    public async Task RestoreAsync(AgentCredentials credentials, CancellationToken cancellationToken)
    {
        var state = await stateStore.LoadAsync(cancellationToken);
        if (state.DesiredState is not null)
        {
            _ = await ApplyAsync(state.DesiredState, credentials, cancellationToken);
        }
    }

    public async Task RefreshRuntimeStatesAsync(CancellationToken cancellationToken)
    {
        foreach (var item in runtimeStates.ToArray())
        {
            try
            {
                var metadata = await instanceStore.TryLoadAsync(item.Key, cancellationToken);
                if (metadata is null || !providers.TryGet(metadata.DesiredState.BackendType, out var provider))
                {
                    continue;
                }

                var process = processSupervisor.GetStatus(item.Key);
                BackendTrafficSnapshot? traffic = item.Value.Traffic;
                var state = await stateStore.LoadAsync(cancellationToken);
                var artifact = FindArtifact(state.DesiredState, metadata.DesiredState);
                if (artifact is not null)
                {
                    var binaryPath = await binaryManager.EnsureAsync(artifact, cancellationToken);
                    var context = CreateContext(metadata.DesiredState, metadata.ConfigFileName, binaryPath);
                    var health = await provider.CheckHealthAsync(context, cancellationToken);
                    traffic = await provider.CollectTrafficAsync(context, cancellationToken) ?? traffic;
                    var status = process.Status == ServiceRuntimeStatus.Running && health.IsHealthy
                        ? ServiceRuntimeStatus.Running
                        : process.Status == ServiceRuntimeStatus.Running
                            ? ServiceRuntimeStatus.Failed
                            : process.Status;
                    runtimeStates[item.Key] = State(item.Key, status, metadata.DesiredState.BackendVersion,
                        metadata.ConfigSha256, traffic,
                        status == ServiceRuntimeStatus.Failed ? "health_check_failed" : null);
                }
                else
                {
                    runtimeStates[item.Key] = State(item.Key, process.Status, metadata.DesiredState.BackendVersion,
                        metadata.ConfigSha256, traffic,
                        process.Status == ServiceRuntimeStatus.Failed ? "process_failed" : null);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsExpectedFailure(exception))
            {
                logger.LogWarning("Runtime refresh failed for service {ServiceId}.", item.Key);
                SetFailed(item.Key, "runtime_refresh_failed");
            }
        }
    }

    private async Task ApplyServiceAsync(ServicePlan plan, List<Guid> changed, CancellationToken cancellationToken)
    {
        var prior = await instanceStore.TryLoadAsync(plan.Desired.ServiceId, cancellationToken);
        var isChanged = prior is null || prior.ConfigSha256 != plan.Config.Sha256 ||
                        !string.Equals(prior.DesiredState.BackendVersion, plan.Desired.BackendVersion,
                            StringComparison.Ordinal) ||
                        !string.Equals(prior.DesiredState.BackendType, plan.Desired.BackendType,
                            StringComparison.Ordinal);

        if (!plan.Desired.Enabled)
        {
            changed.Add(plan.Desired.ServiceId);
            await processSupervisor.StopAsync(plan.Desired.ServiceId, StopTimeout, cancellationToken);
            runtimeStates[plan.Desired.ServiceId] = State(plan.Desired.ServiceId, ServiceRuntimeStatus.Stopped,
                plan.Desired.BackendVersion, prior?.ConfigSha256, null, null);
            return;
        }

        runtimeStates[plan.Desired.ServiceId] = State(plan.Desired.ServiceId, ServiceRuntimeStatus.Installing,
            plan.Desired.BackendVersion, prior?.ConfigSha256, null, null);
        var binaryPath = await binaryManager.EnsureAsync(plan.Artifact!, cancellationToken);
        if (isChanged && prior is not null)
        {
            await instanceStore.SaveLastKnownGoodAsync(plan.Desired.ServiceId, cancellationToken);
        }

        var configPath = await instanceStore.SaveConfigAsync(plan.Desired, plan.Config, cancellationToken);
        changed.Add(plan.Desired.ServiceId);
        var spec = await plan.Provider.CreateProcessSpecAsync(
            new BackendInstanceContext(plan.Desired, instanceStore.GetInstanceDirectory(plan.Desired.ServiceId),
                binaryPath, configPath), cancellationToken);
        var process = isChanged
            ? await processSupervisor.RestartAsync(plan.Desired.ServiceId, spec, StopTimeout, cancellationToken)
            : processSupervisor.GetStatus(plan.Desired.ServiceId).Status == ServiceRuntimeStatus.Running
                ? processSupervisor.GetStatus(plan.Desired.ServiceId)
                : await processSupervisor.StartAsync(plan.Desired.ServiceId, spec, cancellationToken);
        if (process.Status != ServiceRuntimeStatus.Running)
        {
            throw new InvalidOperationException("Backend process could not be started.");
        }

        runtimeStates[plan.Desired.ServiceId] = State(plan.Desired.ServiceId, ServiceRuntimeStatus.Running,
            plan.Desired.BackendVersion, plan.Config.Sha256, null, null);
    }

    private async Task RollbackAsync(IReadOnlyList<Guid> changed, NodeDesiredState desiredState,
        CancellationToken cancellationToken)
    {
        var priorState = await stateStore.LoadAsync(cancellationToken);
        foreach (var serviceId in changed.Reverse())
        {
            try
            {
                await processSupervisor.StopAsync(serviceId, StopTimeout, cancellationToken);
                _ = await instanceStore.TryRollbackAsync(serviceId, cancellationToken);
                if (priorState.DesiredState is null)
                {
                    continue;
                }

                var metadata = await instanceStore.TryLoadAsync(serviceId, cancellationToken);
                if (metadata is null || !metadata.DesiredState.Enabled ||
                    !providers.TryGet(metadata.DesiredState.BackendType, out var provider)) continue;
                var artifact = FindArtifact(priorState.DesiredState, metadata.DesiredState);
                if (artifact is null) continue;
                var binaryPath = await binaryManager.EnsureAsync(artifact, cancellationToken);
                var spec = await provider.CreateProcessSpecAsync(
                    CreateContext(metadata.DesiredState, metadata.ConfigFileName, binaryPath), cancellationToken);
                await processSupervisor.StartAsync(serviceId, spec, cancellationToken);
            }
            catch (Exception)
            {
                logger.LogWarning("Rollback failed for service {ServiceId}.", serviceId);
            }
        }
    }

    private async Task<PreflightResult> PreflightAsync(NodeDesiredState desired, CancellationToken cancellationToken)
    {
        if (desired.Revision < 0 || desired.Services is null || desired.BackendArtifacts is null)
            return PreflightResult.Failed(Guid.Empty, "invalid_desired_state");
        var serviceIds = new HashSet<Guid>();
        var tcpPorts = new HashSet<int>();
        var udpPorts = new HashSet<int>();
        var plans = new List<ServicePlan>(desired.Services.Count);
        foreach (var service in desired.Services)
        {
            if (!IsValidService(service) || !serviceIds.Add(service.ServiceId) ||
                !providers.TryGet(service.BackendType, out var provider))
                return PreflightResult.Failed(service.ServiceId, "invalid_service");
            var matches = desired.BackendArtifacts.Where(artifact =>
                artifact.BackendType == service.BackendType && artifact.Version == service.BackendVersion &&
                artifact.Rid == binaryManager.CurrentRid).ToArray();
            if (service.Enabled && (matches.Length != 1 || !IsValidArtifact(matches[0])))
                return PreflightResult.Failed(service.ServiceId, "artifact_unavailable");
            var validation = await provider.ValidateAsync(service, cancellationToken);
            if (!validation.IsValid || validation.TcpPorts is null || validation.UdpPorts is null ||
                validation.TcpPorts.Any(port => port is < 1 or > 65535 || !tcpPorts.Add(port)) ||
                validation.UdpPorts.Any(port => port is < 1 or > 65535 || !udpPorts.Add(port)))
                return PreflightResult.Failed(service.ServiceId, "invalid_config");
            var config = await provider.RenderConfigAsync(service, cancellationToken);
            if (!IsValidRenderedConfig(config))
                return PreflightResult.Failed(service.ServiceId, "invalid_rendered_config");
            plans.Add(new ServicePlan(service, service.Enabled ? matches[0] : null, provider, config));
        }

        return PreflightResult.Success(plans);
    }

    private BackendInstanceContext
        CreateContext(ServiceDesiredState desired, string configFileName, string binaryPath) =>
        new(desired, instanceStore.GetInstanceDirectory(desired.ServiceId), binaryPath,
            Path.Combine(instanceStore.GetInstanceDirectory(desired.ServiceId), configFileName));

    private static BackendArtifact? FindArtifact(NodeDesiredState? state, ServiceDesiredState service) =>
        state?.BackendArtifacts?.SingleOrDefault(artifact =>
            artifact.BackendType == service.BackendType && artifact.Version == service.BackendVersion &&
            artifact.Rid == RuntimeInformation.RuntimeIdentifier);

    private static bool IsValidService(ServiceDesiredState service) => service.ServiceId != Guid.Empty &&
                                                                       IsSafeComponent(service.Name) &&
                                                                       IsSafeComponent(service.BackendType) &&
                                                                       IsSafeComponent(service.BackendVersion) &&
                                                                       service.ConfigSchemaVersion > 0 &&
                                                                       !string.IsNullOrWhiteSpace(service.ConfigJson);

    private static bool IsValidArtifact(BackendArtifact artifact) => IsSafeComponent(artifact.BackendType) &&
                                                                     IsSafeComponent(artifact.Version) &&
                                                                     IsSafeComponent(artifact.Rid) &&
                                                                     Path.GetFileName(artifact.FileName) ==
                                                                     artifact.FileName &&
                                                                     IsSafeComponent(artifact.FileName) &&
                                                                     artifact.Size > 0 && IsSha256(artifact.Sha256);

    private static bool IsValidRenderedConfig(RenderedBackendConfig config) =>
        Path.GetFileName(config.FileName) == config.FileName && IsSafeComponent(config.FileName) &&
        IsSha256(config.Sha256) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(config.Content.Span),
            Convert.FromHexString(config.Sha256));

    private static bool IsSafeComponent(string value) => !string.IsNullOrWhiteSpace(value) &&
                                                         value.All(character =>
                                                             char.IsAsciiLetterOrDigit(character) ||
                                                             character is '.' or '-' or '_');

    private static bool IsSha256(string value) => value.Length == 64 &&
                                                  value.All(character =>
                                                      character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsExpectedFailure(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or HttpRequestException
        or ArgumentException or KeyNotFoundException;

    private void SetFailed(Guid serviceId, string code)
    {
        if (serviceId != Guid.Empty)
            runtimeStates[serviceId] = State(serviceId, ServiceRuntimeStatus.Failed,
                runtimeStates.TryGetValue(serviceId, out var current) ? current.BackendVersion : null,
                current?.AppliedConfigSha256, current?.Traffic, code);
    }

    private ServiceRuntimeState State(Guid id, ServiceRuntimeStatus status, string? version, string? configSha,
        BackendTrafficSnapshot? traffic, string? errorCode) => new(id, status, version, configSha, traffic,
        timeProvider.GetUtcNow(), errorCode, errorCode is null ? null : SafeMessage(errorCode));

    private static string SafeMessage(string code) => code switch
    {
        "invalid_desired_state" => "Desired state is invalid.",
        "invalid_service" => "A service definition is invalid.",
        "artifact_unavailable" => "A required backend artifact is unavailable.",
        "invalid_config" or "invalid_rendered_config" => "Service configuration validation failed.",
        "runtime_refresh_failed" => "Runtime status could not be refreshed.",
        "health_check_failed" => "Service health check failed.",
        "process_failed" => "Backend process is not running.", _ => "Service reconciliation failed."
    };

    private sealed record ServicePlan(
        ServiceDesiredState Desired,
        BackendArtifact? Artifact,
        IBackendProvider Provider,
        RenderedBackendConfig Config);

    private sealed record PreflightResult(
        bool Succeeded,
        Guid ServiceId,
        string ErrorCode,
        IReadOnlyList<ServicePlan> Plans)
    {
        public static PreflightResult Failed(Guid id, string code) => new(false, id, code, []);

        public static PreflightResult Success(IReadOnlyList<ServicePlan> plans) =>
            new(true, Guid.Empty, string.Empty, plans);
    }
}