namespace HyPanel.Server;

using HyPanel.Server.Persistence;

/// <summary>Applies per-user monthly traffic resets; checked every 10 minutes.</summary>
internal sealed class TrafficResetWorker(SqliteServerRepository repository, ILogger<TrafficResetWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reset = await repository.ResetDueTrafficAsync(stoppingToken);
                if (reset > 0) logger.LogInformation("Reset monthly traffic for {Count} user(s).", reset);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Monthly traffic reset failed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
