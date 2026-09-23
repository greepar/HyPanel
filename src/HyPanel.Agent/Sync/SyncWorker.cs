namespace HyPanel.Agent;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Agent.Reconciliation;
using HyPanel.Agent.Updates;

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
    ServiceLogCollector logCollector,
    ServiceReconciler reconciler,
    BackendProcessSupervisor processSupervisor,
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
            state = await stateStore.LoadAsync(stoppingToken);
            await reconciler.RestoreAsync(credentials, stoppingToken);
            state = await stateStore.LoadAsync(stoppingToken);
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
        var nextDelaySeconds = DefaultIntervalSeconds;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pendingResults = commandState.PendingResults.ToArray();
                await reconciler.RefreshRuntimeStatesAsync(stoppingToken);
                var pendingUsageBatches = usageStateStore.GetPendingBatchesSnapshot();
                var updateReport = await updateStateStore.GetReportAsync(stoppingToken);
                var response = await SyncAsync(credentials, state.AppliedRevision, reconciler.GetRuntimeStates(),
                    pendingUsageBatches, pendingResults, updateReport, stoppingToken);
                await updater.MarkSyncSucceededAsync(stoppingToken);
                await usageStateStore.AcknowledgeAsync(pendingUsageBatches, response.AcceptedUsageBatchIds, stoppingToken);
                if (response.DesiredState is not null)
                {
                    var result = await reconciler.ApplyAsync(response.DesiredState, credentials, stoppingToken);
                    if (result.Succeeded) state = await stateStore.LoadAsync(stoppingToken);
                }

                if (pendingResults.Length > 0)
                {
                    commandState = commandState with
                    {
                        PendingResults = commandState.PendingResults.Where(result =>
                            result.Status == AgentCommandStatus.Running ||
                            !pendingResults.Contains(result)).ToArray()
                    };
                    await commandStateStore.SaveAsync(commandState, stoppingToken);
                }

                commandState = await ProcessCommandsAsync(response.Commands, commandState, credentials, stoppingToken);
                if (response.AgentUpdate is not null)
                {
                    await updater.ApplyOfferAsync(response.AgentUpdate, credentials, stoppingToken);
                }
                backoffSeconds = 0;
                nextDelaySeconds = response.SyncIntervalSeconds is >= 1 and <= 300
                    ? response.SyncIntervalSeconds
                    : credentials.SyncIntervalSeconds is >= 1 and <= 300
                        ? credentials.SyncIntervalSeconds
                        : DefaultIntervalSeconds;
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
            await Task.Delay(TimeSpan.FromSeconds(nextDelaySeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds),
                timeProvider, stoppingToken);
        }
    }

    private async Task<AgentSyncResponse> SyncAsync(AgentCredentials credentials, long appliedRevision,
        IReadOnlyList<ServiceRuntimeState> services, IReadOnlyList<UsageBatch> usageBatches,
        IReadOnlyList<AgentCommandResult> results,
        AgentUpdateReport? updateReport,
        CancellationToken cancellationToken)
    {
        var publicIpv4 = await publicIpv4Resolver.GetAsync(cancellationToken);
        var request = new AgentSyncRequest(BuildInfo.Version, BuildInfo.RuntimeIdentifier, appliedRevision,
            metricsCollector.Collect(), services, usageBatches, results, updateReport, publicIpv4);
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
                null, null);
            commandState = commandState with { PendingResults = commandState.PendingResults.Append(running).ToArray() };
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
        return state with { PendingResults = pending, RecentCompletedIds = recent };
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
                return Failed(command.CommandId, startedAt, "log_collection_failed", "Service logs could not be collected.");
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
                    timeProvider.GetUtcNow(), null, null, null)
                : Failed(command.CommandId, startedAt, "credentials_unavailable", "Persisted credentials are unavailable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            return Failed(command.CommandId, startedAt, "health_check_failed", "Agent metrics could not be collected.");
        }
    }

    private AgentCommandResult Failed(Guid commandId, DateTimeOffset startedAt, string code, string message) =>
        new(commandId, AgentCommandStatus.Failed, startedAt, timeProvider.GetUtcNow(), code, message, null);

    private static bool IsTransient(Exception exception) => exception is HttpRequestException or TaskCanceledException;
    private sealed class AgentCredentialException : Exception;
    private sealed class AgentRevokedException : Exception;

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
