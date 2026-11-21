using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Networking;
using HyPanel.Agent.Reconciliation;
using HyPanel.Agent.Updates;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

namespace HyPanel.Agent;

public sealed class SyncWorker(
    ILogger<SyncWorker> logger,
    AgentIdentityManager identityManager,
    AgentCredentialStore credentialStore,
    AgentStateStore stateStore,
    AgentCommandStateStore commandStateStore,
    AgentUsageStateStore usageStateStore,
    AgentUpdateStateStore updateStateStore,
    AgentUpdater updater,
    NodeMetricsCollector metricsCollector,
    PublicIpv4Resolver publicIpv4Resolver,
    PublicIpv6Resolver publicIpv6Resolver,
    UpstreamExitCountryProbe exitCountryProbe,
    ServiceLogCollector logCollector,
    ServiceReconciler reconciler,
    BackendProcessSupervisor processSupervisor,
    PortHoppingManager portHopping,
    TcpPortRelay tcpRelay,
    FirewallManager firewall,
    AgentEnrollmentOptions enrollmentOptions,
    HttpClient httpClient,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    private static readonly Uri SyncPath = new("/api/agent/v1/sync", UriKind.Relative);
    private const int DefaultIntervalSeconds = 8;
    private const int MaxRecentCommands = 1024;
    private const int CredentialRejectedRetrySeconds = 300;
    internal const string RevokedMarkerFileName = "revoked";
    private static readonly TimeSpan SyncRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PanelMoveRetryDelay = TimeSpan.FromMinutes(10);
    private static readonly Uri WatchPath = new("/api/agent/v1/watch", UriKind.Relative);
    private static readonly TimeSpan WatchRequestTimeout = TimeSpan.FromSeconds(45);
    /// <summary>New backend output syncs early, but not more often than this.</summary>
    private static readonly TimeSpan LogSyncMinimumGap = TimeSpan.FromSeconds(2);
    /// <summary>After applying a change, report the resulting service status this soon instead of a full interval later.</summary>
    private const int ReportAfterApplySeconds = 1;
    private const int MaximumLogBytesPerSync = 256 * 1024;
    private DateTimeOffset nextPanelMoveAttempt;

    // Live logs: lines are numbered per service; the Panel answers with the last number it holds for this stream.
    private readonly Guid logStreamId = Guid.NewGuid();
    private Dictionary<Guid, long> logCursors = [];
    private bool panelTakesLogs = true;
    private HashSet<Guid> logServiceIds = [];
    private DateTimeOffset nextLogProbe;
    private readonly Dictionary<Guid, (string ConfigJson, IReadOnlyList<string> Secrets)> logSecrets = [];

    // Early wake-ups of the sync loop: the Panel's watch says something changed, or a backend wrote output.
    private readonly SemaphoreSlim wake = new(0, 1);
    private volatile bool logsPending;
    private AgentCredentials? watchCredentials;
    private long lastSeenRevision = -1;
    private TaskCompletionSource syncCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentCredentials credentials;
        AgentLocalState state;
        AgentCommandStoreState commandState;
        if (File.Exists(Path.Combine(enrollmentOptions.DataDirectory, RevokedMarkerFileName)))
        {
            logger.LogCritical("This Agent's Node was deleted from the Panel. Uninstall the Agent or reinstall it with a new enrollment command.");
            applicationLifetime.StopApplication();
            return;
        }

        try
        {
            await updater.RecoverAsync(stoppingToken);
            credentials = await identityManager.EnsureIdentityAsync(stoppingToken);
            await reconciler.RestoreAsync(credentials, stoppingToken);
            state = await stateStore.LoadAsync(stoppingToken);
            await portHopping.ApplyAsync(state.DesiredState, stoppingToken);
            commandState = await commandStateStore.LoadAsync(stoppingToken);
            commandState = await FinalizeInterruptedCommandsAsync(commandState, stoppingToken);
            await usageStateStore.LoadAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            Environment.ExitCode = 1;
            await updater.RollbackStartupFailureAsync(CancellationToken.None);
            throw;
        }

        var backoffSeconds = 0;
        processSupervisor.LogWritten += OnLogWritten;
        watchCredentials = credentials;
        _ = Task.Run(() => WatchLoopAsync(stoppingToken), CancellationToken.None);

        while (!stoppingToken.IsCancellationRequested)
        {
            int nextDelaySeconds;
            try
            {
                var pendingResults = commandState.PendingResults.ToArray();
                await reconciler.RefreshRuntimeStatesAsync(stoppingToken);
                var pendingUsageBatches = usageStateStore.GetPendingBatchesSnapshot();
                var updateReport = await updateStateStore.GetReportAsync(stoppingToken);
                var sendLogs = panelTakesLogs || timeProvider.GetUtcNow() >= nextLogProbe;
                logsPending = false;
                var logs = sendLogs ? await CollectLogsAsync(stoppingToken) : null;
                var response = await SyncAsync(credentials, state.AppliedRevision, WithPortHoppingErrors(reconciler.GetRuntimeStates()),
                    pendingUsageBatches, pendingResults, updateReport, stoppingToken, logs);
                lastSeenRevision = response.DesiredRevision;
                if (logs is not null) AcceptLogCursors(response.LogCursors);
                var reportSoon = false;
                await updater.MarkSyncSucceededAsync(stoppingToken);
                await usageStateStore.AcknowledgeAsync(pendingUsageBatches, response.AcceptedUsageBatchIds, stoppingToken);
                if (response.DesiredState is not null)
                {
                    var result = await reconciler.ApplyAsync(response.DesiredState, credentials, stoppingToken);
                    if (result.Succeeded)
                    {
                        state = await stateStore.LoadAsync(stoppingToken);
                        await portHopping.ApplyAsync(state.DesiredState, stoppingToken);
                    }
                    reportSoon = true;
                }

                if (response.Relays is not null)
                {
                    var unbound = tcpRelay.Apply(response.Relays);
                    // The relay listens in user space, so the host firewall has to let the port in.
                    await firewall.ApplyAsync("relay", [.. response.Relays.Where(rule => !unbound.ContainsKey(rule.ListenPort))
                        .Select(rule => new FirewallOpening("tcp", rule.ListenPort, null)).Distinct()],
                        stoppingToken);
                }

                if (pendingResults.Length > 0)
                {
                    commandState = commandState with
                    {
                        PendingResults = [.. commandState.PendingResults.Where(result =>
                            result.Status == AgentCommandStatus.Running ||
                            !pendingResults.Contains(result))]
                    };
                    await commandStateStore.SaveAsync(commandState, stoppingToken);
                }

                if (response.Commands.Count > 0) reportSoon = true;
                commandState = await ProcessCommandsAsync(response.Commands, commandState, credentials, stoppingToken);
                if (response.AgentUpdate is not null)
                {
                    await updater.ApplyOfferAsync(response.AgentUpdate, credentials, stoppingToken);
                }
                if (response.PanelUrl is { } panelUrl)
                    credentials = await TryMovePanelAsync(credentials, panelUrl, state.AppliedRevision, stoppingToken);
                watchCredentials = credentials;
                backoffSeconds = 0;
                nextDelaySeconds = response.SyncIntervalSeconds is >= 1 and <= 300
                    ? response.SyncIntervalSeconds
                    : credentials.SyncIntervalSeconds is >= 1 and <= 300
                        ? credentials.SyncIntervalSeconds
                        : DefaultIntervalSeconds;
                if (reportSoon) nextDelaySeconds = Math.Min(nextDelaySeconds, ReportAfterApplySeconds);
                Interlocked.Exchange(ref syncCompleted,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            }
            catch (AgentRevokedException)
            {
                await StandDownAsync();
                return;
            }
            catch (AgentCredentialException)
            {
                // A rejected credential is usually a Panel-side problem (restored backup, proxy misconfiguration).
                // Keep managed services running and retry slowly so the node heals once the Panel is fixed.
                var firstFailure = backoffSeconds < CredentialRejectedRetrySeconds;
                backoffSeconds = CredentialRejectedRetrySeconds;
                nextDelaySeconds = CredentialRejectedRetrySeconds;
                if (firstFailure)
                    logger.LogCritical("Agent credentials were rejected by the Panel; retrying every {Seconds} seconds.",
                        CredentialRejectedRetrySeconds);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                var firstFailure = backoffSeconds == 0;
                backoffSeconds = backoffSeconds == 0 ? 2 : Math.Min(backoffSeconds * 2, 60);
                nextDelaySeconds = backoffSeconds;
                if (firstFailure)
                    logger.LogWarning("Agent sync failed; retrying with bounded backoff.");
                else
                    logger.LogDebug(exception, "Agent sync remains unavailable; retrying with bounded backoff.");
            }
            catch (Exception exception)
            {
                var firstFailure = backoffSeconds == 0;
                backoffSeconds = backoffSeconds == 0 ? 2 : Math.Min(backoffSeconds * 2, 60);
                nextDelaySeconds = backoffSeconds;
                if (firstFailure)
                    logger.LogWarning("Agent sync response was rejected; retrying with bounded backoff.");
                else
                    logger.LogDebug(exception, "Agent sync response remains invalid; retrying with bounded backoff.");
            }

            var jitterMilliseconds = Random.Shared.Next(0, 1001);
            var delay = TimeSpan.FromSeconds(nextDelaySeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds);
            // A healthy link wakes early for Panel changes and new output; a failing one keeps its backoff.
            if (backoffSeconds == 0) await WaitForWakeAsync(delay, stoppingToken);
            else await Task.Delay(delay, timeProvider, stoppingToken);
        }
    }

    private async Task WaitForWakeAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        try
        {
            if (logsPending)
            {
                wake.Wait(0);
                // Output already waiting: sync after the minimum gap rather than the full interval.
                await Task.Delay(delay < LogSyncMinimumGap ? delay : LogSyncMinimumGap, timeProvider, cancellationToken);
                return;
            }
            if (!await wake.WaitAsync(delay, cancellationToken)) return;
            var elapsed = timeProvider.GetUtcNow() - started;
            // Wake-ups from output are spaced out; a Panel change goes right away (logsPending stays false for it).
            if (logsPending && elapsed < LogSyncMinimumGap)
                await Task.Delay(LogSyncMinimumGap - elapsed, timeProvider, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Wake()
    {
        try
        {
            if (wake.CurrentCount == 0) wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void OnLogWritten()
    {
        if (!panelTakesLogs || logsPending) return;
        logsPending = true;
        Wake();
    }

    /// <summary>
    /// Holds a long-poll open with the Panel; when it reports a change (an admin saved something), the sync loop runs
    /// at once instead of waiting out its interval. A Panel without the endpoint is asked again every 10 minutes.
    /// </summary>
    private async Task WatchLoopAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.Zero;
            try
            {
                var waitForSync = syncCompleted.Task;
                if (watchCredentials is { } credentials && lastSeenRevision >= 0)
                {
                    var changed = await WatchOnceAsync(credentials, lastSeenRevision, stoppingToken);
                    failures = 0;
                    if (changed is null) delay = PanelMoveRetryDelay;
                    else if (changed.Value)
                    {
                        Wake();
                        // Watch again only after that sync, or a stale revision would return at once in a loop.
                        await waitForSync.WaitAsync(TimeSpan.FromSeconds(30), timeProvider, stoppingToken)
                            .ContinueWith(static _ => { }, TaskScheduler.Default);
                    }
                }
                else delay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                failures++;
                delay = TimeSpan.FromSeconds(Math.Min(60, 2 << Math.Min(failures, 5)));
                logger.LogDebug(exception, "Panel watch failed; retrying.");
            }

            try
            {
                // Never spin: at least a short pause between watches.
                await Task.Delay(delay > TimeSpan.FromMilliseconds(250) ? delay : TimeSpan.FromMilliseconds(250),
                    timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>True when the Panel has news, false when the wait ran out, null when the Panel has no watch endpoint.</summary>
    private async Task<bool?> WatchOnceAsync(AgentCredentials credentials, long revision, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get,
            new Uri(new Uri(credentials.PanelBaseUrl), $"{WatchPath}?revision={revision}"));
        message.Headers.Add("X-HyPanel-Agent-Id", credentials.AgentId.ToString("D"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AgentSecret);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(WatchRequestTimeout);
        using var response = await httpClient.SendAsync(message, timeout.Token);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed) return null;
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
        return JsonSerializer.Deserialize(bytes, HyPanelJsonSerializerContext.Default.AgentWatchResponse)?.Changed ?? false;
    }

    /// <summary>New output of every backend since the Panel's cursors, with secrets from the service config masked.</summary>
    private async Task<IReadOnlyList<ServiceLogChunk>> CollectLogsAsync(CancellationToken cancellationToken)
    {
        var desired = (await stateStore.LoadAsync(cancellationToken)).DesiredState;
        var configs = (desired?.Services ?? []).ToDictionary(service => service.ServiceId, service => service.ConfigJson);
        logServiceIds = [.. configs.Keys];
        var chunks = new List<ServiceLogChunk>();
        var budget = MaximumLogBytesPerSync;
        foreach (var (serviceId, firstSequence, lines) in processSupervisor.LogsAfter(logCursors))
        {
            // Output of a service the Panel no longer has would be refused and resent forever.
            if (!configs.ContainsKey(serviceId)) continue;
            if (budget <= 0) break;
            var secrets = configs.TryGetValue(serviceId, out var config) ? SecretsFor(serviceId, config) : [];
            var taken = new List<string>(lines.Count);
            foreach (var line in lines.Take(1024))
            {
                var redacted = ServiceLogCollector.Redact(line, secrets);
                budget -= System.Text.Encoding.UTF8.GetByteCount(redacted) + 8;
                if (budget < 0 && taken.Count > 0) break;
                taken.Add(redacted);
            }
            chunks.Add(new ServiceLogChunk(serviceId, firstSequence, taken));
        }
        return chunks;
    }

    private IReadOnlyList<string> SecretsFor(Guid serviceId, string configJson)
    {
        if (logSecrets.TryGetValue(serviceId, out var cached) && ReferenceEquals(cached.ConfigJson, configJson))
            return cached.Secrets;
        IReadOnlyList<string> secrets;
        try
        {
            secrets = ServiceLogCollector.ReadSecrets(configJson);
        }
        catch (JsonException)
        {
            secrets = [];
        }
        logSecrets[serviceId] = (configJson, secrets);
        return secrets;
    }

    private void AcceptLogCursors(IReadOnlyList<ServiceLogCursor>? cursors)
    {
        if (cursors is null)
        {
            // An older Panel ignores the logs; stop sending them and ask again later (it may get upgraded).
            panelTakesLogs = false;
            nextLogProbe = timeProvider.GetUtcNow() + PanelMoveRetryDelay;
            return;
        }
        var wasOff = !panelTakesLogs;
        panelTakesLogs = true;
        logCursors = cursors.ToDictionary(cursor => cursor.ServiceId, cursor => cursor.Sequence);
        // More output than one sync carries: keep going soon.
        if (wasOff || processSupervisor.LogsAfter(logCursors).Any(item => logServiceIds.Contains(item.ServiceId)))
            logsPending = true;
    }

    /// <summary>
    /// Switches to the panel address the Panel advertises, but only after an authenticated sync at that address
    /// succeeds; until then the current address keeps working and the move is retried every 10 minutes.
    /// </summary>
    internal async Task<AgentCredentials> TryMovePanelAsync(AgentCredentials credentials, string panelUrl,
        long appliedRevision, CancellationToken cancellationToken)
    {
        if (PanelMoveTarget(credentials.PanelBaseUrl, panelUrl) is not { } normalized ||
            timeProvider.GetUtcNow() < nextPanelMoveAttempt) return credentials;

        var candidate = credentials with { PanelBaseUrl = normalized };
        try
        {
            await SyncAsync(candidate, appliedRevision, WithPortHoppingErrors(reconciler.GetRuntimeStates()), [], [],
                await updateStateStore.GetReportAsync(cancellationToken), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException ||
                                          !cancellationToken.IsCancellationRequested)
        {
            nextPanelMoveAttempt = timeProvider.GetUtcNow() + PanelMoveRetryDelay;
            logger.LogWarning("The Panel moved to {PanelUrl}, but it is not reachable yet; staying on {Current}.",
                normalized, credentials.PanelBaseUrl);
            return credentials;
        }

        await credentialStore.SaveAsync(candidate, cancellationToken);
        logger.LogInformation("Panel address changed from {Previous} to {PanelUrl}.", credentials.PanelBaseUrl, normalized);
        return candidate;
    }

    /// <summary>The https origin to move to, or null when the advertised address is invalid or already in use.</summary>
    internal static string? PanelMoveTarget(string current, string advertised)
    {
        if (!Uri.TryCreate(advertised, UriKind.Absolute, out var target) || target.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(target.Host) || !string.IsNullOrEmpty(target.UserInfo)) return null;
        var normalized = target.GetLeftPart(UriPartial.Authority);
        return string.Equals(normalized, current.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ? null : normalized;
    }

    private async Task<AgentSyncResponse> SyncAsync(AgentCredentials credentials, long appliedRevision,
        IReadOnlyList<ServiceRuntimeState> services, IReadOnlyList<UsageBatch> usageBatches,
        IReadOnlyList<AgentCommandResult> results,
        AgentUpdateReport? updateReport,
        CancellationToken cancellationToken,
        IReadOnlyList<ServiceLogChunk>? logs = null)
    {
        var publicIpv4 = await publicIpv4Resolver.GetAsync(cancellationToken);
        var publicIpv6 = await publicIpv6Resolver.GetAsync(cancellationToken);
        var desired = (await stateStore.LoadAsync(cancellationToken)).DesiredState;
        var exitCountries = exitCountryProbe.Observe(desired?.Services ?? []);
        if (exitCountries.Services.Count > 0 || exitCountries.Routes.Count > 0)
            services = [.. services.Select(state => state with
            {
                ExitCountryCode = exitCountries.Services.GetValueOrDefault(state.ServiceId) ?? state.ExitCountryCode,
                RouteExitCountries = exitCountries.Routes.GetValueOrDefault(state.ServiceId)
            })];
        var request = new AgentSyncRequest(BuildInfo.Version, BuildInfo.RuntimeIdentifier, appliedRevision,
            metricsCollector.Collect(), services, usageBatches, results, updateReport, publicIpv4,
            publicIpv4Resolver.CountryCode, CpuFeatures.SupportsX64V3, desired?.BackendArtifacts ?? [], publicIpv6,
            logs is null ? null : logStreamId, logs);
        using var message =
            new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(credentials.PanelBaseUrl), SyncPath));
        message.Headers.Add("X-HyPanel-Agent-Id", credentials.AgentId.ToString("D"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AgentSecret);
        message.Content =
            new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request,
                HyPanelJsonSerializerContext.Default.AgentSyncRequest));
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SyncRequestTimeout);
        using var response = await httpClient.SendAsync(message, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Gone)
        {
            throw new AgentRevokedException();
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AgentCredentialException();
        }

        if ((int)response.StatusCode >= 500)
        {
            throw new HttpRequestException($"Panel returned HTTP {(int)response.StatusCode}.");
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var syncResponse = JsonSerializer.Deserialize(bytes, HyPanelJsonSerializerContext.Default.AgentSyncResponse)
                           ?? throw new JsonException("The sync response was empty.");
        if (syncResponse.Commands is null || syncResponse.Commands.Any(command => !Enum.IsDefined(command.Type)))
        {
            throw new JsonException("The sync response contains an unknown command type.");
        }

        return syncResponse;
    }

    private async Task<AgentCommandStoreState> ProcessCommandsAsync(IReadOnlyList<AgentCommand> commands,
        AgentCommandStoreState commandState, AgentCredentials credentials, CancellationToken cancellationToken)
    {
        foreach (var command in commands)
        {
            if (commandState.RecentCompletedIds.Contains(command.CommandId) ||
                commandState.PendingResults.Any(result => result.CommandId == command.CommandId))
            {
                continue;
            }

            var startedAt = timeProvider.GetUtcNow();
            if (IsExpired(command, startedAt))
            {
                var expired = Failed(command.CommandId, startedAt, "command_expired",
                    "Command expired before execution.");
                commandState = Complete(commandState, expired);
                await commandStateStore.SaveAsync(commandState, cancellationToken);
                continue;
            }
            var running = new AgentCommandResult(command.CommandId, AgentCommandStatus.Running, startedAt, null, null,
                null);
            commandState = commandState with { PendingResults = [.. commandState.PendingResults, running] };
            await commandStateStore.SaveAsync(commandState, cancellationToken);

            var result = await ExecuteCommandAsync(command, credentials, startedAt, cancellationToken);
            commandState = Complete(commandState, result);
            await commandStateStore.SaveAsync(commandState, cancellationToken);
        }

        return commandState;
    }

    public static bool IsExpired(AgentCommand command, DateTimeOffset now) =>
        command.ExpiresAt is { } expiresAt && expiresAt <= now;

    private async Task<AgentCommandStoreState> FinalizeInterruptedCommandsAsync(AgentCommandStoreState state,
        CancellationToken cancellationToken)
    {
        var running = state.PendingResults.Where(result => result.Status == AgentCommandStatus.Running).ToArray();
        foreach (var result in running)
        {
            state = Complete(state,
                result with
                {
                    Status = AgentCommandStatus.Failed, CompletedAt = timeProvider.GetUtcNow(),
                    ErrorCode = "interrupted", ErrorMessage = "Agent restarted before command completion."
                });
        }

        if (running.Length > 0) await commandStateStore.SaveAsync(state, cancellationToken);
        return state;
    }

    private static AgentCommandStoreState Complete(AgentCommandStoreState state, AgentCommandResult result)
    {
        var pending = state.PendingResults.Where(item => item.CommandId != result.CommandId).Append(result).ToArray();
        var recent = state.RecentCompletedIds.Where(id => id != result.CommandId).Append(result.CommandId)
            .TakeLast(MaxRecentCommands).ToArray();
        return new AgentCommandStoreState(pending, recent);
    }

    private async Task<AgentCommandResult> ExecuteCommandAsync(
        AgentCommand command,
        AgentCredentials credentials,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        if (command.Type == AgentCommandType.CollectServiceLogs)
        {
            if (command.TargetServiceId is not { } serviceId)
                return Failed(command.CommandId, startedAt, "target_required", "A target service is required.");
            try
            {
                var output = await logCollector.CollectAsync(serviceId, cancellationToken);
                return output is null
                    ? Failed(command.CommandId, startedAt, "service_not_managed", "The service is not managed by this Agent.")
                    : new AgentCommandResult(command.CommandId, AgentCommandStatus.Succeeded, startedAt,
                        timeProvider.GetUtcNow(), null, null, output);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                   or InvalidDataException or JsonException)
            {
                return Failed(command.CommandId, startedAt, "log_collection_failed",
                    $"Service logs could not be collected: {exception.GetType().Name}: {exception.Message}");
            }
        }

        if (command.Type != AgentCommandType.RunHealthCheck)
        {
            return Failed(command.CommandId, startedAt, "unsupported_command", "Command is not supported.");
        }

        try
        {
            var persistedCredentials = await credentialStore.TryLoadAsync(cancellationToken);
            _ = metricsCollector.Collect();
            var healthy = persistedCredentials is not null &&
                          persistedCredentials.AgentId == credentials.AgentId &&
                          !string.IsNullOrWhiteSpace(persistedCredentials.AgentSecret);
            return healthy
                ? new AgentCommandResult(command.CommandId, AgentCommandStatus.Succeeded, startedAt,
                    timeProvider.GetUtcNow(), null, null)
                : Failed(command.CommandId, startedAt, "credentials_unavailable", "Persisted credentials are unavailable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            return Failed(command.CommandId, startedAt, "health_check_failed",
                $"Agent metrics could not be collected: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private AgentCommandResult Failed(Guid commandId, DateTimeOffset startedAt, string code, string message) =>
        new(commandId, AgentCommandStatus.Failed, startedAt, timeProvider.GetUtcNow(), code,
            message.Length > 1000 ? message[..1000] + "…" : message);

    private static bool IsTransient(Exception exception) => exception is HttpRequestException or TaskCanceledException;
    private sealed class AgentCredentialException : Exception;
    private sealed class AgentRevokedException : Exception;

    /// <summary>Surfaces a port-hopping failure on the affected services without changing their run status.</summary>
    private IReadOnlyList<ServiceRuntimeState> WithPortHoppingErrors(IReadOnlyList<ServiceRuntimeState> states) =>
        portHopping.LastError is not { } error
            ? states
            : [.. states.Select(state => portHopping.HoppingServices.Contains(state.ServiceId) && state.ErrorCode is null
                ? state with { ErrorCode = "port_hopping_failed", ErrorMessage = error }
                : state)];

    /// <summary>
    /// The Node was deleted in the Panel: stop every managed backend, forget the identity and local
    /// service state, and exit cleanly so the service manager does not restart the Agent.
    /// </summary>
    private async Task StandDownAsync()
    {
        logger.LogCritical("This Agent's Node was deleted from the Panel; stopping managed services and standing down.");
        try { await processSupervisor.StopAllAsync(CancellationToken.None); }
        catch (Exception exception) { logger.LogError(exception, "Some managed services could not be stopped."); }
        var dataDirectory = enrollmentOptions.DataDirectory;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dataDirectory, RevokedMarkerFileName),
                timeProvider.GetUtcNow().ToString("O"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "The revocation marker could not be written.");
        }
        foreach (var name in new[]
                 {
                     AgentCredentialStore.CredentialsFileName, "state.json", "command-state.json", "usage-state.json",
                     AgentUpdateStateStore.FileName
                 })
            TryDelete(() => File.Delete(Path.Combine(dataDirectory, name)));
        TryDelete(() => Directory.Delete(Path.Combine(dataDirectory, "services"), recursive: true));
        Environment.ExitCode = 0;
        applicationLifetime.StopApplication();
    }

    private static void TryDelete(Action delete)
    {
        try { delete(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }
}
