using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;

namespace Mogu.Web.Tracking;

/// <summary>Info hashes the tracker accepts: torrents in the database that aren't banned.</summary>
public interface ITrackerWhitelist
{
    ValueTask<bool> IsAllowedAsync(byte[] infoHash, CancellationToken cancellationToken);

    /// <summary>Starts tracking a torrent right away (e.g. after upload or unban).</summary>
    void Allow(byte[] infoHash);

    /// <summary>Stops tracking a torrent right away and drops its peers (e.g. after a ban).</summary>
    void Revoke(byte[] infoHash);
}

/// <summary>
/// In-memory whitelist. Hits are answered from memory; misses fall back to one database lookup (with a short
/// negative cache), so freshly uploaded torrents work before any refresh. It is kept current by
/// <see cref="Allow"/>/<see cref="Revoke"/>, by applying queued <see cref="TrackerWhitelistEvent"/> rows, and by
/// periodic full reloads. Deleted-but-not-banned torrents stay tracked, as with the original tracker feed.
/// </summary>
public sealed class TrackerWhitelist(
    IServiceScopeFactory scopeFactory,
    ITrackerPeerStore peerStore,
    IOptions<TrackerOptions> options,
    TimeProvider timeProvider,
    ILogger<TrackerWhitelist> logger) : ITrackerWhitelist
{
    private const int MaxUnknownEntries = 100_000;
    private const int EventBatchSize = 1000;

    private readonly ConcurrentDictionary<string, byte> allowed = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> unknown = new(StringComparer.Ordinal);

    public int Count => allowed.Count;

    public async ValueTask<bool> IsAllowedAsync(byte[] infoHash, CancellationToken cancellationToken)
    {
        var hex = Convert.ToHexStringLower(infoHash);
        if (allowed.ContainsKey(hex))
        {
            return true;
        }

        var now = timeProvider.GetUtcNow();
        if (unknown.TryGetValue(hex, out var expires) && expires > now)
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
        var exists = await db.Torrents.AsNoTracking()
            .AnyAsync(t => t.InfoHash == infoHash && !t.IsBanned, cancellationToken);

        if (exists)
        {
            Allow(infoHash);
        }
        else
        {
            if (unknown.Count >= MaxUnknownEntries)
            {
                unknown.Clear();
            }

            unknown[hex] = now + options.Value.UnregisteredCacheDuration;
        }

        return exists;
    }

    public void Allow(byte[] infoHash)
    {
        var hex = Convert.ToHexStringLower(infoHash);
        allowed[hex] = 0;
        unknown.TryRemove(hex, out _);
    }

    public void Revoke(byte[] infoHash)
    {
        var hex = Convert.ToHexStringLower(infoHash);
        allowed.TryRemove(hex, out _);
        peerStore.RemoveSwarm(hex);
    }

    /// <summary>Replaces the whitelist with the current database state.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
        var hashes = await db.Torrents.AsNoTracking()
            .Where(t => !t.IsBanned)
            .Select(t => t.InfoHash)
            .ToListAsync(cancellationToken);

        var current = hashes.Select(Convert.ToHexStringLower).ToHashSet(StringComparer.Ordinal);
        foreach (var hex in current)
        {
            allowed[hex] = 0;
        }

        foreach (var hex in allowed.Keys)
        {
            if (!current.Contains(hex))
            {
                allowed.TryRemove(hex, out _);
                peerStore.RemoveSwarm(hex);
            }
        }

        unknown.Clear();
        logger.LogDebug("Tracker whitelist reloaded with {Count} torrents", current.Count);
    }

    /// <summary>Applies queued <see cref="TrackerWhitelistEvent"/> rows in order and deletes them.</summary>
    /// <returns>The number of events applied.</returns>
    public async Task<int> ProcessEventsAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
        var events = await db.TrackerWhitelistEvents.AsNoTracking()
            .OrderBy(e => e.Id)
            .Take(EventBatchSize)
            .ToListAsync(cancellationToken);

        if (events.Count == 0)
        {
            return 0;
        }

        foreach (var whitelistEvent in events)
        {
            if (whitelistEvent.Action == TrackerWhitelistAction.Insert)
            {
                Allow(whitelistEvent.InfoHash);
            }
            else
            {
                Revoke(whitelistEvent.InfoHash);
            }
        }

        var lastId = events[^1].Id;
        await db.TrackerWhitelistEvents.Where(e => e.Id <= lastId).ExecuteDeleteAsync(cancellationToken);
        logger.LogDebug("Applied {Count} tracker whitelist events", events.Count);
        return events.Count;
    }
}
