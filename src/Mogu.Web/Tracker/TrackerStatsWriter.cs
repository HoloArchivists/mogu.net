using Microsoft.EntityFrameworkCore;
using Mogu.Core.Data;

namespace Mogu.Web.Tracker;

/// <summary>Persists swarm counts and completions from the peer store into <see cref="Core.Entities.Torrent"/>.</summary>
public sealed class TrackerStatsWriter(
    IServiceScopeFactory scopeFactory,
    ITrackerPeerStore peerStore,
    TimeProvider timeProvider,
    ILogger<TrackerStatsWriter> logger)
{
    private const int ResetBatchSize = 500;

    /// <summary>Writes changed seeders/leechers and pending completions in one transaction.</summary>
    /// <returns>The number of torrents updated.</returns>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        var changes = peerStore.TakeStatsChanges();
        if (changes.Count == 0)
        {
            return 0;
        }

        var persisted = false;
        var updated = 0;
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            foreach (var change in changes)
            {
                var infoHash = Convert.FromHexString(change.InfoHashHex);
                var delta = change.DownloadsDelta;
                updated += await db.Torrents
                    .Where(t => t.InfoHash == infoHash)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(t => t.SeedCount, change.Seeders)
                            .SetProperty(t => t.LeechCount, change.Leechers)
                            .SetProperty(t => t.DownloadCount, t => t.DownloadCount + delta)
                            .SetProperty(t => t.StatsUpdatedAt, now),
                        cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            persisted = true;
        }
        finally
        {
            peerStore.CompleteStatsChanges(changes, persisted);
        }

        logger.LogDebug("Flushed tracker stats for {Updated} of {Changed} torrents", updated, changes.Count);
        return updated;
    }

    /// <summary>
    /// Zeroes counts left over from before a restart for torrents that have no peers now. Only meaningful once
    /// every live peer had a chance to re-announce (one peer timeout after startup).
    /// </summary>
    /// <returns>The number of torrents reset.</returns>
    public async Task<int> ResetInactiveAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
        var withStats = await db.Torrents.AsNoTracking()
            .Where(t => t.SeedCount != 0 || t.LeechCount != 0)
            .Select(t => new { t.Id, t.InfoHash })
            .ToListAsync(cancellationToken);

        var active = peerStore.GetActiveInfoHashes();
        var stale = withStats
            .Where(t => !active.Contains(Convert.ToHexStringLower(t.InfoHash)))
            .Select(t => t.Id)
            .ToList();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var reset = 0;
        foreach (var batch in stale.Chunk(ResetBatchSize))
        {
            reset += await db.Torrents
                .Where(t => batch.Contains(t.Id))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.SeedCount, 0)
                        .SetProperty(t => t.LeechCount, 0)
                        .SetProperty(t => t.StatsUpdatedAt, now),
                    cancellationToken);
        }

        if (reset > 0)
        {
            logger.LogInformation("Reset stale tracker stats of {Count} torrents without peers", reset);
        }

        return reset;
    }
}
