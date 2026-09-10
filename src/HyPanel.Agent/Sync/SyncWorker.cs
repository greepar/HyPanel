namespace HyPanel.Agent;

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;
using HyPanel.Agent.Reconciliation;

public sealed class SyncWorker(
    ILogger<SyncWorker> logger,
    AgentIdentityManager identityManager,
    AgentCredentialStore credentialStore,
    AgentStateStore stateStore,
    AgentCommandStateStore commandStateStore,
    AgentUsageStateStore usageStateStore,
    NodeMetricsCollector metricsCollector,
    ServiceReconciler reconciler,
    HttpClient httpClient,
    TimeProvider timeProvider,
    IHostApplicationLifetime applicationLifetime) : BackgroundService
{
    private static readonly Uri SyncPath = new("/api/agent/v1/sync", UriKind.Relative);
    private const int DefaultIntervalSeconds = 8;
    private const int MaxRecentCommands = 1024;
    private static readonly TimeSpan SyncRequestTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        AgentCredentials credentials;
        try
        {
            credentials = await identityManager.EnsureIdentityAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        var state = await stateStore.LoadAsync(stoppingToken);
        await reconciler.RestoreAsync(credentials, stoppingToken);
        state = await stateStore.LoadAsync(stoppingToken);
        var commandState = await commandStateStore.LoadAsync(stoppingToken);
        commandState = await FinalizeInterruptedCommandsAsync(commandState, stoppingToken);
        await usageStateStore.LoadAsync(stoppingToken);
        var backoffSeconds = 0;
        var nextDelaySeconds = DefaultIntervalSeconds;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pendingResults = commandState.PendingResults.ToArray();
                var pendingUsageBatches = usageStateStore.GetPendingBatchesSnapshot();
                await reconciler.RefreshRuntimeStatesAsync(stoppingToken);
                var response = await SyncAsync(credentials, state.AppliedRevision, reconciler.GetRuntimeStates(),
                    pendingUsageBatches, pendingResults, stoppingToken);
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
                backoffSeconds = 0;
                nextDelaySeconds = response.SyncIntervalSeconds is >= 1 and <= 300
                    ? response.SyncIntervalSeconds
                    : credentials.SyncIntervalSeconds is >= 1 and <= 300
                        ? credentials.SyncIntervalSeconds
                        : DefaultIntervalSeconds;
            }
            catch (AgentCredentialException)
            {
                logger.LogCritical("Agent credentials were rejected by the Panel; stopping the Agent.");
                applicationLifetime.StopApplication();
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                backoffSeconds = backoffSeconds == 0 ? 2 : Math.Min(backoffSeconds * 2, 60);
                nextDelaySeconds = backoffSeconds;
                logger.LogWarning(exception, "Agent sync failed; retrying with backoff.");
            }
            catch (Exception exception)
            {
                backoffSeconds = backoffSeconds == 0 ? 2 : Math.Min(backoffSeconds * 2, 60);
                nextDelaySeconds = backoffSeconds;
                logger.LogWarning(exception, "Agent sync response was rejected; retrying with backoff.");
            }

            var jitterMilliseconds = Random.Shared.Next(0, 1001);
            await Task.Delay(TimeSpan.FromSeconds(nextDelaySeconds) + TimeSpan.FromMilliseconds(jitterMilliseconds),
                timeProvider, stoppingToken);
        }
    }

    private async Task<AgentSyncResponse> SyncAsync(AgentCredentials credentials, long appliedRevision,
        IReadOnlyList<ServiceRuntimeState> services, IReadOnlyList<UsageBatch> usageBatches,
        IReadOnlyList<AgentCommandResult> results,
        CancellationToken cancellationToken)
    {
        var request = new AgentSyncRequest(GetAgentVersion(), GetPlatform(), appliedRevision,
            metricsCollector.Collect(), services, usageBatches, results);
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
            var running = new AgentCommandResult(command.CommandId, AgentCommandStatus.Running, startedAt, null, null,
                null);
            commandState = commandState with { PendingResults = commandState.PendingResults.Append(running).ToArray() };
            await commandStateStore.SaveAsync(commandState, cancellationToken);

            var result = await ExecuteCommandAsync(command, credentials, startedAt, cancellationToken);
            commandState = Complete(commandState, result);
            await commandStateStore.SaveAsync(commandState, cancellationToken);
        }

        return commandState;
    }

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
        if (command.Type != AgentCommandType.RunHealthCheck)
        {
            return new AgentCommandResult(command.CommandId, AgentCommandStatus.Failed, startedAt,
                timeProvider.GetUtcNow(), "unsupported_command", "Command is not supported.");
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
                : new AgentCommandResult(command.CommandId, AgentCommandStatus.Failed, startedAt,
                    timeProvider.GetUtcNow(), "credentials_unavailable", "Persisted credentials are unavailable.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidOperationException)
        {
            return new AgentCommandResult(command.CommandId, AgentCommandStatus.Failed, startedAt,
                timeProvider.GetUtcNow(), "health_check_failed", "Agent metrics could not be collected.");
        }
    }

    private static bool IsTransient(Exception exception) => exception is HttpRequestException or TaskCanceledException;
    private static string GetAgentVersion() => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static string GetPlatform()
    {
        var operatingSystem = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsMacOS() ? "osx"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        return $"{operatingSystem}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    }

    private sealed class AgentCredentialException : Exception;
}
