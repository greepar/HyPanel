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
    AgentUsageStateStore usageStateStore,
    TimeProvider timeProvider,
    ILogger<ServiceReconciler> logger)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartHealthDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StableProcessWindow = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, ServiceRuntimeState> runtimeStates = new();
    private readonly ConcurrentDictionary<Guid, RecoveryState> recoveryStates = new();
    private readonly ConcurrentDictionary<Guid, string> reportedFailures = new();
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

            if (preflight.FatalErrorCode is not null)
            {
                return new ApplyResult(false, preflight.FatalErrorCode, SafeMessage(preflight.FatalErrorCode));
            }

            var previous = await stateStore.LoadAsync(cancellationToken);
            var failed = preflight.Failures.Count > 0;
            var firstErrorCode = preflight.Failures.FirstOrDefault()?.ErrorCode;
            var appliedChanges = new List<Guid>();
            Guid failedService = Guid.Empty;
            foreach (var failure in preflight.Failures)
            {
                failedService = failure.ServiceId;
                SetFailed(failure.ServiceId, failure.ErrorCode);
            }
            try
            {
                // Services deleted in the Panel are stopped first and always, even when another service fails to
                // apply: otherwise a deleted backend keeps running and holding ports the new services need.
                if (previous.DesiredState is not null)
                {
                    var desiredIds = desiredState.Services.Select(service => service.ServiceId).ToHashSet();
                    foreach (var removed in previous.DesiredState.Services.Where(service =>
                                 !desiredIds.Contains(service.ServiceId)))
                    {
                        try
                        {
                            var stopped =
                                await processSupervisor.StopAsync(removed.ServiceId, StopTimeout, cancellationToken);
                            if (stopped.Status == ServiceRuntimeStatus.Failed)
                                throw new InvalidOperationException("A removed backend process could not be stopped.");
                            instanceStore.Delete(removed.ServiceId);
                            recoveryStates.TryRemove(removed.ServiceId, out _);
                            runtimeStates[removed.ServiceId] = State(removed.ServiceId, ServiceRuntimeStatus.Stopped,
                                removed.BackendVersion, null, null, null);
                        }
                        catch (Exception exception) when (IsExpectedFailure(exception))
                        {
                            failed = true;
                            firstErrorCode ??= ErrorCode(exception);
                            failedService = removed.ServiceId;
                            SetFailed(removed.ServiceId, ErrorCode(exception));
                        }
                    }
                }

                foreach (var plan in preflight.Plans)
                {
                    var changed = new List<Guid>();
                    try
                    {
                        await ApplyServiceAsync(plan, changed, cancellationToken);
                        appliedChanges.AddRange(changed);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (changed.Count > 0) await RollbackAsync(changed, CancellationToken.None);
                        throw;
                    }
                    catch (Exception exception) when (IsExpectedFailure(exception))
                    {
                        failed = true;
                        firstErrorCode ??= ErrorCode(exception);
                        failedService = plan.Desired.ServiceId;
                        if (exception is not BackendBackoffException)
                            RegisterRecoveryFailure(plan.Desired.ServiceId, RecoveryKey(plan));
                        LogFailureOnce(plan.Desired.ServiceId, ErrorCode(exception));
                        await RollbackAsync(changed, cancellationToken);
                        if (exception is BackendBackoffException)
                            continue;
                        if (!await SetRolledBackAsync(plan.Desired.ServiceId, desiredState, cancellationToken))
                            SetFailed(plan.Desired.ServiceId, ErrorCode(exception));
                    }
                }

                if (failed)
                {
                    var recoveryState = await BuildRecoveryStateAsync(previous.DesiredState, desiredState,
                        cancellationToken);
                    await stateStore.SaveAsync(new AgentLocalState(previous.AppliedRevision,
                            WithoutTlsMaterial(recoveryState)),
                        cancellationToken);
                }
                else
                    await stateStore.SaveAsync(new AgentLocalState(desiredState.Revision,
                        WithoutTlsMaterial(desiredState)), cancellationToken);
                return failed
                    ? new ApplyResult(false, firstErrorCode ?? "apply_failed",
                        SafeMessage(firstErrorCode ?? "apply_failed"))
                    : new ApplyResult(true, null, null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var code = ErrorCode(exception);
                logger.LogWarning("Desired state persistence failed with error code {ErrorCode}.", code);
                if (appliedChanges.Count > 0) await RollbackAsync(appliedChanges, CancellationToken.None);
                SetFailed(failedService, code);
                return new ApplyResult(false, code, SafeMessage(code));
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
                    if (metadata.DesiredState.Enabled && process.Status != ServiceRuntimeStatus.Running)
                    {
                        await RecoverProcessAsync(item.Key, metadata, binaryPath, context, item.Value.Traffic,
                            cancellationToken);
                        continue;
                    }
                    var health = await provider.CheckHealthAsync(context, cancellationToken);
                    if (!health.IsHealthy && process.Status == ServiceRuntimeStatus.Running)
                    {
                        await processSupervisor.StopAsync(item.Key, StopTimeout, cancellationToken);
                        RegisterRecoveryFailure(item.Key, RecoveryKey(metadata));
                        runtimeStates[item.Key] = State(item.Key, ServiceRuntimeStatus.Backoff,
                            metadata.DesiredState.BackendVersion, metadata.ConfigSha256, traffic,
                            "backend_restart_backoff");
                        continue;
                    }
                    var userTraffic = await provider.CollectUserTrafficAsync(context, cancellationToken);
                    if (userTraffic.Count > 0)
                    {
                        await usageStateStore.RecordCumulativeAsync(item.Key, userTraffic, cancellationToken);
                        traffic = new BackendTrafficSnapshot(userTraffic.Sum(value => value.UploadBytes),
                            userTraffic.Sum(value => value.DownloadBytes), userTraffic.Max(value => value.ObservedAt));
                    }
                    var status = process.Status == ServiceRuntimeStatus.Running && health.IsHealthy
                        ? ServiceRuntimeStatus.Running
                        : process.Status == ServiceRuntimeStatus.Running
                            ? ServiceRuntimeStatus.Failed
                            : process.Status;
                    var errorCode = item.Value.ErrorCode == "backend_update_rolled_back"
                        ? item.Value.ErrorCode
                        : status == ServiceRuntimeStatus.Failed ? "health_check_failed" : null;
                    runtimeStates[item.Key] = State(item.Key, status, metadata.DesiredState.BackendVersion,
                        metadata.ConfigSha256, traffic, errorCode);
                    if (status == ServiceRuntimeStatus.Running &&
                        recoveryStates.TryGetValue(item.Key, out var recovery) &&
                        timeProvider.GetUtcNow() - recovery.StartedAt >= StableProcessWindow)
                    {
                        recoveryStates.TryRemove(item.Key, out _);
                    }
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
        await usageStateStore.EnsureBaselinesAsync(plan.Desired.ServiceId, plan.Desired.Users, cancellationToken);
        await instanceStore.EnsureTlsAssetAsync(plan.Desired, cancellationToken);
        var prior = await instanceStore.TryLoadAsync(plan.Desired.ServiceId, cancellationToken);
        var isChanged = prior is null || prior.ConfigSha256 != plan.Config.Sha256 ||
                        prior.DesiredState.Enabled != plan.Desired.Enabled ||
                        !string.Equals(prior.DesiredState.BackendVersion, plan.Desired.BackendVersion,
                            StringComparison.Ordinal) ||
                        !string.Equals(prior.DesiredState.BackendType, plan.Desired.BackendType,
                            StringComparison.Ordinal);
        var recoveryKey = RecoveryKey(plan);
        if (recoveryStates.TryGetValue(plan.Desired.ServiceId, out var blocked)
            && blocked.Key == recoveryKey && timeProvider.GetUtcNow() < blocked.NextAttemptAt
            && (isChanged || processSupervisor.GetStatus(plan.Desired.ServiceId).Status != ServiceRuntimeStatus.Running))
        {
            runtimeStates[plan.Desired.ServiceId] = State(plan.Desired.ServiceId, ServiceRuntimeStatus.Backoff,
                plan.Desired.BackendVersion, prior?.ConfigSha256, null, "backend_restart_backoff");
            throw new BackendBackoffException();
        }

        if (!plan.Desired.Enabled)
        {
            if (isChanged && prior is not null)
                await instanceStore.SaveLastKnownGoodAsync(plan.Desired.ServiceId, cancellationToken);
            if (isChanged)
            {
                await instanceStore.SaveConfigAsync(plan.Desired, plan.Config, cancellationToken);
                changed.Add(plan.Desired.ServiceId);
            }
            if (processSupervisor.GetStatus(plan.Desired.ServiceId).Status == ServiceRuntimeStatus.Running)
            {
                if (!changed.Contains(plan.Desired.ServiceId)) changed.Add(plan.Desired.ServiceId);
                await processSupervisor.StopAsync(plan.Desired.ServiceId, StopTimeout, cancellationToken);
            }
            recoveryStates.TryRemove(plan.Desired.ServiceId, out _);
            runtimeStates[plan.Desired.ServiceId] = State(plan.Desired.ServiceId, ServiceRuntimeStatus.Stopped,
                plan.Desired.BackendVersion, isChanged ? plan.Config.Sha256 : prior?.ConfigSha256, null, null);
            return;
        }

        runtimeStates[plan.Desired.ServiceId] = State(plan.Desired.ServiceId, ServiceRuntimeStatus.Installing,
            plan.Desired.BackendVersion, prior?.ConfigSha256, null, null);
        var binaryPath = await binaryManager.EnsureAsync(plan.Artifact!, cancellationToken);
        if (isChanged && prior is not null)
        {
            await instanceStore.SaveLastKnownGoodAsync(plan.Desired.ServiceId, cancellationToken);
        }

        var configPath = isChanged
            ? await instanceStore.SaveConfigAsync(plan.Desired, plan.Config, cancellationToken)
            : Path.Combine(instanceStore.GetInstanceDirectory(plan.Desired.ServiceId), prior!.ConfigFileName);
        if (isChanged) changed.Add(plan.Desired.ServiceId);
        var wasRunning = processSupervisor.GetStatus(plan.Desired.ServiceId).Status == ServiceRuntimeStatus.Running;
        if (!wasRunning && !changed.Contains(plan.Desired.ServiceId)) changed.Add(plan.Desired.ServiceId);
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

        await Task.Delay(StartHealthDelay, timeProvider, cancellationToken);
        process = processSupervisor.GetStatus(plan.Desired.ServiceId);
        var health = await plan.Provider.CheckHealthAsync(
            new BackendInstanceContext(plan.Desired, instanceStore.GetInstanceDirectory(plan.Desired.ServiceId),
                binaryPath, configPath), cancellationToken);
        if (process.Status != ServiceRuntimeStatus.Running || !health.IsHealthy)
            throw new InvalidOperationException("Backend process did not pass the post-start health window.");

        runtimeStates[plan.Desired.ServiceId] = State(plan.Desired.ServiceId, ServiceRuntimeStatus.Running,
            plan.Desired.BackendVersion, plan.Config.Sha256, null, null);
        await instanceStore.PruneTlsAssetsAsync(plan.Desired.ServiceId, cancellationToken);
        recoveryStates.TryRemove(plan.Desired.ServiceId, out _);
        reportedFailures.TryRemove(plan.Desired.ServiceId, out _);
    }

    private async Task RecoverProcessAsync(Guid serviceId, BackendInstanceMetadata metadata, string binaryPath,
        BackendInstanceContext context, BackendTrafficSnapshot? traffic,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var key = $"{metadata.DesiredState.BackendVersion}:{metadata.ConfigSha256}";
        var recovery = recoveryStates.AddOrUpdate(serviceId,
            _ => new RecoveryState(key, 0, now, now),
            (_, current) => current.Key == key ? current : new RecoveryState(key, 0, now, now));
        if (now < recovery.NextAttemptAt)
        {
            runtimeStates[serviceId] = State(serviceId, ServiceRuntimeStatus.Backoff,
                metadata.DesiredState.BackendVersion, metadata.ConfigSha256, traffic, "backend_restart_backoff");
            return;
        }

        var attempt = Math.Min(recovery.Attempts + 1, 6);
        var delay = TimeSpan.FromSeconds(Math.Min(60, 1 << attempt));
        recoveryStates[serviceId] = new RecoveryState(key, attempt, now + delay, recovery.StartedAt);
        runtimeStates[serviceId] = State(serviceId, ServiceRuntimeStatus.Restarting,
            metadata.DesiredState.BackendVersion, metadata.ConfigSha256, traffic, "backend_restarting");
        var provider = providers.GetRequired(metadata.DesiredState.BackendType);
        var spec = await provider.CreateProcessSpecAsync(context, cancellationToken);
        var started = await processSupervisor.StartAsync(serviceId, spec, cancellationToken);
        if (started.Status != ServiceRuntimeStatus.Running)
        {
            runtimeStates[serviceId] = State(serviceId, ServiceRuntimeStatus.Backoff,
                metadata.DesiredState.BackendVersion, metadata.ConfigSha256, traffic, "backend_restart_backoff");
            return;
        }

        await Task.Delay(StartHealthDelay, timeProvider, cancellationToken);
        var health = await provider.CheckHealthAsync(context, cancellationToken);
        if (processSupervisor.GetStatus(serviceId).Status == ServiceRuntimeStatus.Running && health.IsHealthy)
        {
            runtimeStates[serviceId] = State(serviceId, ServiceRuntimeStatus.Running,
                metadata.DesiredState.BackendVersion, metadata.ConfigSha256, traffic, null);
            return;
        }

        await processSupervisor.StopAsync(serviceId, StopTimeout, cancellationToken);
        runtimeStates[serviceId] = State(serviceId, ServiceRuntimeStatus.Backoff,
            metadata.DesiredState.BackendVersion, metadata.ConfigSha256, traffic, "backend_restart_backoff");
    }

    private async Task RollbackAsync(IReadOnlyList<Guid> changed, CancellationToken cancellationToken)
    {
        var priorState = await stateStore.LoadAsync(cancellationToken);
        foreach (var serviceId in changed.Reverse())
        {
            try
            {
                await processSupervisor.StopAsync(serviceId, StopTimeout, cancellationToken);
                var rolledBack = await instanceStore.TryRollbackAsync(serviceId, cancellationToken);
                var priorService = priorState.DesiredState?.Services.SingleOrDefault(service => service.ServiceId == serviceId);
                if (!rolledBack && priorService is null)
                {
                    instanceStore.Delete(serviceId);
                    continue;
                }
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
                await instanceStore.PruneTlsAssetsAsync(serviceId, cancellationToken);
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
            return PreflightResult.Fatal("invalid_desired_state");
        var serviceIds = new HashSet<Guid>();
        var tcpPorts = new HashSet<int>();
        var udpPorts = new HashSet<int>();
        var plans = new List<ServicePlan>(desired.Services.Count);
        var failures = new List<ServiceFailure>();
        foreach (var service in desired.Services)
        {
            if (!IsValidService(service) || !serviceIds.Add(service.ServiceId) ||
                !providers.TryGet(service.BackendType, out var provider))
            {
                failures.Add(new ServiceFailure(service.ServiceId, "invalid_service"));
                continue;
            }

            try
            {
                var matches = desired.BackendArtifacts.Where(artifact =>
                    artifact.BackendType == service.BackendType && artifact.Version == service.BackendVersion &&
                    artifact.Rid == binaryManager.CurrentRid).ToArray();
                if (service.Enabled && (matches.Length != 1 || !IsValidArtifact(matches[0])))
                {
                    failures.Add(new ServiceFailure(service.ServiceId, "artifact_unavailable"));
                    continue;
                }
                var validation = await provider.ValidateAsync(service, cancellationToken);
                if (!validation.IsValid || validation.TcpPorts is null || validation.UdpPorts is null ||
                    validation.TcpPorts.Any(port => port is < 1 or > 65535 || tcpPorts.Contains(port)) ||
                    validation.UdpPorts.Any(port => port is < 1 or > 65535 || udpPorts.Contains(port)))
                {
                    failures.Add(new ServiceFailure(service.ServiceId, "invalid_config"));
                    continue;
                }
                foreach (var port in validation.TcpPorts) tcpPorts.Add(port);
                foreach (var port in validation.UdpPorts) udpPorts.Add(port);
                var config = await provider.RenderConfigAsync(service, cancellationToken);
                if (!IsValidRenderedConfig(config))
                {
                    failures.Add(new ServiceFailure(service.ServiceId, "invalid_rendered_config"));
                    continue;
                }
                plans.Add(new ServicePlan(service, service.Enabled ? matches[0] : null, provider, config));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                failures.Add(new ServiceFailure(service.ServiceId, "invalid_config"));
            }
        }

        return PreflightResult.Complete(plans, failures);
    }

    private async Task<NodeDesiredState> BuildRecoveryStateAsync(NodeDesiredState? previous, NodeDesiredState attempted,
        CancellationToken cancellationToken)
    {
        var ids = attempted.Services.Select(service => service.ServiceId)
            .Concat(previous?.Services.Select(service => service.ServiceId) ?? [])
            .Distinct()
            .ToArray();
        var services = new List<ServiceDesiredState>(ids.Length);
        foreach (var id in ids)
        {
            var metadata = await instanceStore.TryLoadAsync(id, cancellationToken);
            if (metadata is not null) services.Add(metadata.DesiredState);
        }

        var availableArtifacts = attempted.BackendArtifacts
            .Concat(previous?.BackendArtifacts ?? [])
            .GroupBy(artifact => (artifact.BackendType, artifact.Version, artifact.Rid))
            .Select(group => group.First())
            .ToArray();
        var artifacts = services.Where(service => service.Enabled)
            .Select(service => availableArtifacts.SingleOrDefault(artifact =>
                artifact.BackendType == service.BackendType && artifact.Version == service.BackendVersion &&
                artifact.Rid == binaryManager.CurrentRid))
            .Where(artifact => artifact is not null)
            .Cast<BackendArtifact>()
            .DistinctBy(artifact => (artifact.BackendType, artifact.Version, artifact.Rid))
            .ToArray();
        return new NodeDesiredState(previous?.Revision ?? 0, services, artifacts);
    }

    private static NodeDesiredState WithoutTlsMaterial(NodeDesiredState state) => state with
    {
        Services = state.Services.Select(BackendInstanceStore.WithoutTlsMaterial).ToArray()
    };

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

    private static string ErrorCode(Exception exception) =>
        exception is IOException ioException && (ioException.HResult & 0xffff) is 28 or 112
            ? "disk_full"
            : exception is BackendBackoffException
                ? "backend_restart_backoff"
            : "apply_failed";

    private void SetFailed(Guid serviceId, string code)
    {
        if (serviceId != Guid.Empty)
            runtimeStates[serviceId] = State(serviceId, ServiceRuntimeStatus.Failed,
                runtimeStates.TryGetValue(serviceId, out var current) ? current.BackendVersion : null,
                current?.AppliedConfigSha256, current?.Traffic, code);
    }

    private void LogFailureOnce(Guid serviceId, string code)
    {
        if (reportedFailures.TryGetValue(serviceId, out var current) && current == code) return;
        reportedFailures[serviceId] = code;
        logger.LogWarning("Service reconciliation failed for {ServiceId} with error code {ErrorCode}.", serviceId, code);
    }

    private void RegisterRecoveryFailure(Guid serviceId, string key)
    {
        var now = timeProvider.GetUtcNow();
        recoveryStates.AddOrUpdate(serviceId,
            _ => new RecoveryState(key, 1, now + TimeSpan.FromSeconds(2), now),
            (_, current) =>
            {
                var attempts = current.Key == key ? Math.Min(current.Attempts + 1, 6) : 1;
                return new RecoveryState(key, attempts,
                    now + TimeSpan.FromSeconds(Math.Min(60, 1 << attempts)), now);
            });
    }

    private static string RecoveryKey(ServicePlan plan) =>
        $"{plan.Desired.BackendType}:{plan.Desired.BackendVersion}:{plan.Desired.Enabled}:{plan.Config.Sha256}";

    private static string RecoveryKey(BackendInstanceMetadata metadata) =>
        $"{metadata.DesiredState.BackendType}:{metadata.DesiredState.BackendVersion}:" +
        $"{metadata.DesiredState.Enabled}:{metadata.ConfigSha256}";

    private async Task<bool> SetRolledBackAsync(Guid serviceId, NodeDesiredState attempted,
        CancellationToken cancellationToken)
    {
        if (serviceId == Guid.Empty) return false;
        var target = attempted.Services.SingleOrDefault(item => item.ServiceId == serviceId);
        var restored = await instanceStore.TryLoadAsync(serviceId, cancellationToken);
        if (target is null || restored is null || target.BackendVersion == restored.DesiredState.BackendVersion)
            return false;
        var process = processSupervisor.GetStatus(serviceId);
        runtimeStates[serviceId] = State(serviceId,
            process.Status == ServiceRuntimeStatus.Running ? ServiceRuntimeStatus.Running : ServiceRuntimeStatus.Failed,
            restored.DesiredState.BackendVersion, restored.ConfigSha256,
            runtimeStates.TryGetValue(serviceId, out var current) ? current.Traffic : null,
            "backend_update_rolled_back");
        return true;
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
        "process_failed" => "Backend process is not running.",
        "backend_update_rolled_back" => "Backend update failed and the previous version was restored.",
        "backend_restarting" => "Backend process is restarting.",
        "backend_restart_backoff" => "Backend restart is delayed after repeated failures.",
        "disk_full" => "The Agent data disk does not have enough free space.",
        _ => "Service reconciliation failed."
    };

    private sealed record ServicePlan(
        ServiceDesiredState Desired,
        BackendArtifact? Artifact,
        IBackendProvider Provider,
        RenderedBackendConfig Config);

    private sealed record RecoveryState(string Key, int Attempts, DateTimeOffset NextAttemptAt,
        DateTimeOffset StartedAt);

    private sealed class BackendBackoffException : InvalidOperationException;

    private sealed record ServiceFailure(Guid ServiceId, string ErrorCode);

    private sealed record PreflightResult(
        string? FatalErrorCode,
        IReadOnlyList<ServicePlan> Plans,
        IReadOnlyList<ServiceFailure> Failures)
    {
        public static PreflightResult Fatal(string code) => new(code, [], []);

        public static PreflightResult Complete(IReadOnlyList<ServicePlan> plans,
            IReadOnlyList<ServiceFailure> failures) => new(null, plans, failures);
    }
}
