using HyPanel.Server.Live;
using HyPanel.Server.Persistence;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

namespace HyPanel.Server.Endpoints;

/// <summary>
/// Long-poll endpoints that make the panel feel live: the Agent's watch (sync right away when an admin changes
/// something), the admin change counter, and the per-service log tail.
/// </summary>
internal static class LiveEndpoints
{
    internal static readonly TimeSpan LongPollWait = TimeSpan.FromSeconds(25);
    private const int MaximumLogLinesPerRead = 1000;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/agent/v1/watch", WatchAsync);
        endpoints.MapGet("/api/admin/v1/changes", ChangesAsync);
        endpoints.MapGet("/api/admin/v1/nodes/{nodeId:guid}/services/{serviceId:guid}/logs", LogsAsync);
    }

    /// <summary>
    /// Any successful admin write bumps the change counter, so other open tabs reload and the affected Agent's
    /// watch returns. Reads, sign-in and Agent traffic do not.
    /// </summary>
    public static IApplicationBuilder UseAdminChangeSignal(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        await next(context);
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && context.Request.Path.StartsWithSegments("/api/admin")
            && context.Response.StatusCode is >= 200 and < 300)
            context.RequestServices.GetRequiredService<ChangeFeed>().Bump();
    });

    private static async Task<IResult> WatchAsync(HttpRequest request, long? revision, AgentAuthentication authentication,
        SqliteServerRepository repository, ChangeFeed feed, IHostApplicationLifetime lifetime,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var agent = await authentication.AuthenticateAsync(request, cancellationToken);
        if (agent is null) return Results.Unauthorized();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        var deadline = timeProvider.GetUtcNow() + LongPollWait;
        var since = feed.Version;
        var (currentRevision, start) = await repository.GetAgentWatchStateAsync(agent.AgentId, cancellationToken);
        // The Agent says which revision it last saw, so a change made between its sync and this call is not missed.
        if (revision is { } seen && seen != currentRevision) return Changed(true);
        try
        {
            while (timeProvider.GetUtcNow() < deadline)
            {
                since = await feed.WaitAsync(since, deadline - timeProvider.GetUtcNow(), linked.Token);
                var (_, now) = await repository.GetAgentWatchStateAsync(agent.AgentId, linked.Token);
                if (now != start) return Changed(true);
            }
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
        }

        return Changed(false);

        static IResult Changed(bool changed) =>
            Results.Json(new AgentWatchResponse(changed), HyPanelJsonSerializerContext.Default.AgentWatchResponse);
    }

    private static async Task<IResult> ChangesAsync(HttpRequest request, long? since, AdminAuthorization authorization,
        ChangeFeed feed, IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(request, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        var version = feed.Version;
        if (since is { } known && known == version)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
            try
            {
                version = await feed.WaitAsync(known, LongPollWait, linked.Token);
            }
            catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
            {
            }
        }

        return Results.Json(new ChangesResponse(version), ServerJsonSerializerContext.Default.ChangesResponse);
    }

    private static async Task<IResult> LogsAsync(Guid nodeId, Guid serviceId, HttpRequest request, long? after,
        bool? wait, AdminAuthorization authorization, SqliteServerRepository repository, ServiceLogStore store,
        IHostApplicationLifetime lifetime, CancellationToken cancellationToken)
    {
        var access = await authorization.AuthorizeAsync(request, cancellationToken);
        if (access != AdminAccessResult.Allowed) return AdminAuthorization.Failure(access);
        if (!(await repository.GetServicesForNodeAsync(nodeId, cancellationToken)).Any(item => item.Service.Id == serviceId))
            return Results.NotFound();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);
        ServiceLogPage page;
        try
        {
            page = await store.ReadAsync(serviceId, Math.Max(0, after ?? 0), MaximumLogLinesPerRead,
                wait == true ? LongPollWait : TimeSpan.Zero, linked.Token);
        }
        catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            page = await store.ReadAsync(serviceId, Math.Max(0, after ?? 0), MaximumLogLinesPerRead, TimeSpan.Zero,
                CancellationToken.None);
        }

        var response = new ServiceLogResponse(
            [.. page.Lines.Select(line => new ServiceLogLineResponse(line.Number, line.ReceivedAtUtc, line.Text))],
            page.Cursor, page.Reset);
        return Results.Json(response, ServerJsonSerializerContext.Default.ServiceLogResponse);
    }
}

internal sealed record ChangesResponse(long Version);

internal sealed record ServiceLogLineResponse(long Number, DateTimeOffset ReceivedAtUtc, string Text);

internal sealed record ServiceLogResponse(ServiceLogLineResponse[] Lines, long Cursor, bool Reset);
