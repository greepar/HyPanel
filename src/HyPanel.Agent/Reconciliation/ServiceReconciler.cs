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
    ILogger<ServiceReconciler> logger,
    EgressNetworkManager? egressNetwork = null)
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartHealthDelay = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StableProcessWindow = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, ServiceRuntimeState> runtimeStates = new();
    private readonly ConcurrentDictionary<Guid, RecoveryState> recoveryStates = new();
    private readonly ConcurrentDictionary<Guid, string> reportedFailures = new();
    // Panel-assigned control port each service's local port was chosen for (see WithLocalControlPortAsync).
    private readonly ConcurrentDictionary<Guid, int> assignedControlPorts = new();
    private readonly SemaphoreSlim applyGate = new(1, 1);

    private readonly ConcurrentDictionary<Guid, string> blockedEgress = new();
    public EgressNetworkReport? EgressReport => egressNetwork?.Report;
    public Task CleanupEgressAsync(CancellationToken ct) => egressNetwork?.ApplyAsync(new(false, []), ct) ?? Task.CompletedTask;

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
            if (desiredState.Services.Any(s => s.EgressRoute is not null &&
                    previous.DesiredState?.Services.FirstOrDefault(p => p.ServiceId == s.ServiceId)?.EgressRoute != s.EgressRoute))
                // Persist exit intent before changing the running service, so a crash cannot restore a local exit.
                await stateStore.SaveAsync(new AgentLocalState(previous.AppliedRevision, WithoutTlsMaterial(desiredState)), cancellationToken);
            // Stop old services before changing or removing their source route, including a switch from local exit.
            foreach (var service in desiredState.Services)
            {
                var old = previous.DesiredState?.Services.FirstOrDefault(s => s.ServiceId == service.ServiceId);
                if (old?.EgressRoute != service.EgressRoute)
                    await processSupervisor.StopAsync(service.ServiceId, StopTimeout, cancellationToken);
            }
            foreach (var removed in previous.DesiredState?.Services.Where(s =>
                         !desiredState.Services.Any(d => d.ServiceId == s.ServiceId)) ?? [])
                await processSupervisor.StopAsync(removed.ServiceId, StopTimeout, cancellationToken);
            if (egressNetwork is not null) await egressNetwork.ApplyAsync(desiredState.EgressNetwork, cancellationToken);
            blockedEgress.Clear();
            foreach (var service in desiredState.Services.Where(s => s.EgressRoute is not null && s.Enabled))
            {
                var error = egressNetwork is null ? "出口转发管理器不可用。"
                    : egressNetwork.Failures.GetValueOrDefault(service.ServiceId);
                if (error is null && preflight.Failures.FirstOrDefault(f => f.ServiceId == service.ServiceId) is { } failure)
                    error = $"出口服务配置未通过验证（{failure.ErrorCode}）。";
                if (error is null && !(desiredState.EgressNetwork?.Tunnels.Any(t =>
                        !t.IsExit && t.ServiceId == service.ServiceId && t.Slot == service.EgressRoute!.Slot) ?? false))
                    error = "出口隧道配置缺失。";
                if (error is not null)
                {
                    blockedEgress[service.ServiceId] = error;
                    await processSupervisor.StopAsync(service.ServiceId, StopTimeout, cancellationToken);
                    preflight = preflight with
                    {
                        Plans = preflight.Plans.Where(p => p.Desired.ServiceId != service.ServiceId).ToArray(),
                        Failures = preflight.Failures.Append(new ServiceFailure(service.ServiceId, "egress_unavailable")).ToArray()
                    };
                }
            }
            var failed = preflight.Failures.Count > 0;
            var failedServiceIds = preflight.Failures.Select(failure => failure.ServiceId).ToHashSet();
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
                        await ApplyServiceAsync(plan, changed, previous.DesiredState, cancellationToken);
                        appliedChanges.AddRange(changed);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        if (changed.Count > 0) await RollbackSafelyAsync(changed, desiredState, CancellationToken.None);
                        throw;
                    }
                    catch (Exception exception) when (IsExpectedFailure(exception))
                    {
                        failed = true;
                        failedServiceIds.Add(plan.Desired.ServiceId);
                        firstErrorCode ??= ErrorCode(exception);
                        failedService = plan.Desired.ServiceId;
                        if (exception is not BackendBackoffException)
                            RegisterRecoveryFailure(plan.Desired.ServiceId, RecoveryKey(plan));
                        LogFailureOnce(plan.Desired.ServiceId, ErrorCode(exception));
                        if (plan.Desired.EgressRoute is not null)
                        {
                            // Never restart an older direct-outbound config when an exit was requested.
                            await processSupervisor.StopAsync(plan.Desired.ServiceId, StopTimeout, cancellationToken);
                            blockedEgress[plan.Desired.ServiceId] = $"出口服务启动失败（{ErrorCode(exception)}）。";
                            SetFailed(plan.Desired.ServiceId, "egress_unavailable");
                            continue;
                        }
                        await RollbackAsync(changed, cancellationToken);
                        if (exception is BackendBackoffException)
                            continue;
                        if (changed.Count == 0 ||
                            !await SetRolledBackAsync(plan.Desired.ServiceId, desiredState, cancellationToken))
                            SetFailed(plan.Desired.ServiceId, ErrorCode(exception));
                    }
                }

                if (failed)
                {
                    var recoveryState = await BuildRecoveryStateAsync(previous.DesiredState, desiredState,
                        failedServiceIds,
                        cancellationToken);
                    await stateStore.SaveAsync(new AgentLocalState(previous.AppliedRevision,
                            WithoutTlsMaterial(recoveryState)),
                        cancellationToken);
                }
                else
                {
                    await stateStore.SaveAsync(new AgentLocalState(desiredState.Revision,
                        WithoutTlsMaterial(desiredState)), cancellationToken);
                    // Only after a fully applied state: a failed apply may still roll back to an older binary.
                    try
                    {
                        await binaryManager.PruneUnusedAsync(desiredState.BackendArtifacts, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning("Could not remove unused backend binaries: {Error}", exception.Message);
                    }
                }
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
                if (appliedChanges.Count > 0) await RollbackSafelyAsync(appliedChanges, desiredState, CancellationToken.None);
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
            if (blockedEgress.ContainsKey(item.Key))
            {
                await processSupervisor.StopAsync(item.Key, StopTimeout, cancellationToken);
                SetFailed(item.Key, "egress_unavailable");
                continue;
            }
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

    private async Task RollbackSafelyAsync(IReadOnlyList<Guid> ids, NodeDesiredState desired, CancellationToken ct)
    {
        var exitIds = desired.Services.Where(s => s.EgressRoute is not null).Select(s => s.ServiceId).ToHashSet();
        foreach (var id in ids.Where(exitIds.Contains))
        {
            blockedEgress[id] = "出口服务配置未完成，已停止出网。";
            await processSupervisor.StopAsync(id, StopTimeout, ct);
            SetFailed(id, "egress_unavailable");
        }
        await RollbackAsync(ids.Where(id => !exitIds.Contains(id)).ToArray(), ct);
    }

    private async Task ApplyServiceAsync(ServicePlan plan, List<Guid> changed, NodeDesiredState? previousState,
        CancellationToken cancellationToken)
    {
        await usageStateStore.EnsureBaselinesAsync(plan.Desired.ServiceId, plan.Desired.Users, cancellationToken);
        await instanceStore.EnsureTlsAssetAsync(plan.Desired, cancellationToken);
        var prior = await instanceStore.TryLoadAsync(plan.Desired.ServiceId, cancellationToken);
        var priorArtifact = FindArtifact(previousState, plan.Desired);
        var isChanged = prior is null || prior.ConfigSha256 != plan.Config.Sha256 ||
                        plan.Desired.Enabled && priorArtifact != plan.Artifact ||
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
        foreach (var requested in desired.Services)
        {
            if (!IsValidService(requested) || !serviceIds.Add(requested.ServiceId) ||
                !providers.TryGet(requested.BackendType, out var provider))
            {
                failures.Add(new ServiceFailure(requested.ServiceId, "invalid_service"));
                continue;
            }

            try
            {
                var service = await WithLocalControlPortAsync(requested, cancellationToken);
                var matches = desired.BackendArtifacts.Where(artifact =>
                    artifact.BackendType == service.BackendType && artifact.Version == service.BackendVersion &&
                    artifact.Rid == binaryManager.CurrentRid).ToArray();
                if (service.Enabled && (matches.Length != 1 || !IsValidArtifact(matches[0])))
                {
                    failures.Add(new ServiceFailure(service.ServiceId, "artifact_unavailable"));
                    continue;
                }
                var validation = await provider.ValidateAsync(service, cancellationToken);
                string? invalid = null;
                if (!validation.IsValid || validation.TcpPorts is null || validation.UdpPorts is null ||
                    validation.TcpPorts.Any(port => port is < 1 or > 65535) ||
                    validation.UdpPorts.Any(port => port is < 1 or > 65535))
                    invalid = validation.ErrorCode is "invalid_users" or "invalid_control_port" or "unsupported_schema"
                        ? validation.ErrorCode
                        : "invalid_config";
                else if (validation.TcpPorts.Any(tcpPorts.Contains) || validation.UdpPorts.Any(udpPorts.Contains))
                    invalid = "port_conflict";
                if (invalid is not null)
                {
                    logger.LogWarning(
                        "Service {ServiceId} ({BackendType}) failed validation: {ErrorCode} {Detail} (tcp {TcpPorts}, udp {UdpPorts}).",
                        service.ServiceId, service.BackendType, invalid, validation.ErrorMessage,
                        string.Join(",", validation.TcpPorts ?? []), string.Join(",", validation.UdpPorts ?? []));
                    failures.Add(new ServiceFailure(service.ServiceId, invalid));
                    continue;
                }
                foreach (var port in validation.TcpPorts!) tcpPorts.Add(port);
                foreach (var port in validation.UdpPorts!) udpPorts.Add(port);
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
            catch (Exception exception)
            {
                logger.LogWarning("Service {ServiceId} could not be prepared: {Error}: {Message}", requested.ServiceId,
                    exception.GetType().Name, exception.Message);
                failures.Add(new ServiceFailure(requested.ServiceId, "invalid_config"));
            }
        }

        return PreflightResult.Complete(plans, failures);
    }

    /// <summary>
    /// The Panel numbers control ports per node, so another Agent on the same host (for example a native one next to
    /// a Docker one) or any other local program may already hold the assigned loopback port. Keeps the port the
    /// running instance already uses, else the assigned port when it is free, else the previously used one, else any
    /// free port. The choice is stable, so an unchanged service is not restarted on every sync.
    /// </summary>
    private async Task<ServiceDesiredState> WithLocalControlPortAsync(ServiceDesiredState service,
        CancellationToken cancellationToken)
    {
        if (service.ControlPort is not { } assigned || !service.Enabled) return service;
        var current = (await instanceStore.TryLoadAsync(service.ServiceId, cancellationToken))?.DesiredState.ControlPort;
        int port;
        if (current is { } running && assignedControlPorts.TryGetValue(service.ServiceId, out var chosenFor) &&
            chosenFor == assigned && processSupervisor.GetStatus(service.ServiceId).Status == ServiceRuntimeStatus.Running)
            port = running;
        else if (IsLoopbackPortFree(assigned)) port = assigned;
        else if (current is { } previous && previous != assigned && IsLoopbackPortFree(previous)) port = previous;
        else port = FreeLoopbackPort();
        if (port != assigned && current != port)
            logger.LogInformation("Control port {Assigned} of service {ServiceId} is in use on this host; using {Port}.",
                assigned, service.ServiceId, port);
        assignedControlPorts[service.ServiceId] = assigned;
        return port == assigned ? service : service with { ControlPort = port };
    }

    private static bool IsLoopbackPortFree(int port)
    {
        try
        {
            using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        probe.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private async Task<NodeDesiredState> BuildRecoveryStateAsync(NodeDesiredState? previous, NodeDesiredState attempted,
        IReadOnlySet<Guid> failedServiceIds, CancellationToken cancellationToken)
    {
        var ids = attempted.Services.Select(service => service.ServiceId)
            .Concat(previous?.Services.Select(service => service.ServiceId) ?? [])
            .Distinct()
            .ToArray();
        var services = new List<ServiceDesiredState>(ids.Length);
        foreach (var id in ids)
        {
            var metadata = await instanceStore.TryLoadAsync(id, cancellationToken);
            var requested = attempted.Services.FirstOrDefault(s => s.ServiceId == id);
            if (requested?.EgressRoute is not null && failedServiceIds.Contains(id)) services.Add(requested);
            else if (metadata is not null) services.Add(metadata.DesiredState);
        }

        var previousArtifacts = previous?.BackendArtifacts ?? [];
        var artifacts = services.Where(service => service.Enabled)
            .Select(service => (failedServiceIds.Contains(service.ServiceId)
                    ? previousArtifacts.Concat(attempted.BackendArtifacts)
                    : attempted.BackendArtifacts.Concat(previousArtifacts)).FirstOrDefault(artifact =>
                artifact.BackendType == service.BackendType && artifact.Version == service.BackendVersion &&
                artifact.Rid == binaryManager.CurrentRid))
            .Where(artifact => artifact is not null)
            .Cast<BackendArtifact>()
            .DistinctBy(artifact => (artifact.BackendType, artifact.Version, artifact.Rid))
            .ToArray();
        return new NodeDesiredState(previous?.Revision ?? 0, services, artifacts, attempted.EgressNetwork);
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
        if (target is null || restored is null)
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
        BackendTrafficSnapshot? traffic, string? errorCode)
    {
        var message = errorCode is null ? null : SafeMessage(errorCode);
        if (errorCode == "egress_unavailable" && blockedEgress.TryGetValue(id, out var reason))
        {
            const string prefix = "出口网络不可用，服务已停止出网：";
            var detail = reason.Replace('\r', ' ').Replace('\n', ' ').Trim();
            message = prefix + detail[..Math.Min(detail.Length, 1024 - prefix.Length)];
        }
        return new ServiceRuntimeState(id, status, version, configSha, traffic,
            timeProvider.GetUtcNow(), errorCode, message);
    }

    private static string SafeMessage(string code) => code switch
    {
        "egress_unavailable" => "出口网络不可用，服务已停止出网；请查看出口节点状态和两端 Agent 的网络错误。",
        "invalid_desired_state" => "Desired state is invalid.",
        "invalid_service" => "A service definition is invalid.",
        "artifact_unavailable" => "A required backend artifact is unavailable.",
        "invalid_config" or "invalid_rendered_config" => "Service configuration validation failed.",
        "invalid_users" => "The service's user credentials are invalid.",
        "invalid_control_port" => "The service's control port is invalid.",
        "unsupported_schema" => "The service configuration schema is not supported by this Agent.",
        "port_conflict" => "Another service on this node already uses one of this service's ports.",
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
