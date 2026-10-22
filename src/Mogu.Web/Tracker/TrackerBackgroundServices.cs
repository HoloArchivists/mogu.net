using Microsoft.Extensions.Options;
using Mogu.Core.Options;

namespace Mogu.Web.Tracker;

/// <summary>Drops peers that stopped announcing.</summary>
public sealed class TrackerPeerPruningService(
    ITrackerPeerStore peerStore,
    IOptions<TrackerOptions> options,
    TimeProvider timeProvider,
    ILogger<TrackerPeerPruningService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(options.Value.PruneInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var result = peerStore.PruneExpired();
                if (result.RemovedPeers > 0)
                {
                    logger.LogDebug(
                        "Pruned {RemovedPeers} expired tracker peers and {RemovedSwarms} empty swarms",
                        result.RemovedPeers,
                        result.RemovedSwarms);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Periodically writes swarm stats to the database, and once more on shutdown.</summary>
public sealed class TrackerStatsFlushService(
    TrackerStatsWriter writer,
    IOptions<TrackerOptions> options,
    TimeProvider timeProvider,
    ILogger<TrackerStatsFlushService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        // Counts persisted before a restart are only trustworthy again once every live peer re-announced.
        var resetStaleAt = timeProvider.GetUtcNow() + settings.PeerTimeout;
        var staleReset = false;

        using var timer = new PeriodicTimer(settings.StatsFlushInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunSafelyAsync(() => writer.FlushAsync(stoppingToken), stoppingToken);

                if (!staleReset && timeProvider.GetUtcNow() >= resetStaleAt)
                {
                    staleReset = await RunSafelyAsync(() => writer.ResetInactiveAsync(stoppingToken), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }

        // Don't lose pending completions on a graceful shutdown.
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await RunSafelyAsync(() => writer.FlushAsync(shutdown.Token), shutdown.Token);
    }

    private async Task<bool> RunSafelyAsync(Func<Task<int>> action, CancellationToken cancellationToken)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested || ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Writing tracker stats failed");
            return false;
        }
    }
}

/// <summary>Loads the whitelist on startup, applies queued whitelist events and reloads it periodically.</summary>
public sealed class TrackerWhitelistSyncService(
    TrackerWhitelist whitelist,
    IOptions<TrackerOptions> options,
    TimeProvider timeProvider,
    ILogger<TrackerWhitelistSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        var nextReload = DateTimeOffset.MinValue;
        var tick = settings.ConsumeWhitelistEvents ? settings.WhitelistEventPollInterval : settings.WhitelistRefreshInterval;
        using var timer = new PeriodicTimer(tick, timeProvider);
        try
        {
            do
            {
                try
                {
                    if (timeProvider.GetUtcNow() >= nextReload)
                    {
                        // Events queued before the reload are already reflected in the database.
                        if (settings.ConsumeWhitelistEvents)
                        {
                            await whitelist.ProcessEventsAsync(stoppingToken);
                        }

                        await whitelist.ReloadAsync(stoppingToken);
                        nextReload = timeProvider.GetUtcNow() + settings.WhitelistRefreshInterval;
                    }
                    else if (settings.ConsumeWhitelistEvents)
                    {
                        await whitelist.ProcessEventsAsync(stoppingToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(ex, "Synchronising the tracker whitelist failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
