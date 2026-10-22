using System.ComponentModel.DataAnnotations;

namespace Mogu.Core.Options;

/// <summary>Built-in HTTP BitTorrent tracker, bound from the <c>Tracker</c> configuration section.</summary>
public sealed class TrackerOptions : IValidatableObject
{
    public const string SectionName = "Tracker";

    /// <summary>Serve <c>/announce</c> and <c>/scrape</c> and run the tracker background services.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Public announce URL of this tracker. When empty, <c>&lt;scheme&gt;://&lt;host&gt;&lt;pathbase&gt;/announce</c>
    /// of the current request is used.
    /// </summary>
    public string? AnnounceUrl { get; set; }

    /// <summary>How often clients are asked to re-announce.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "1.00:00:00")]
    public TimeSpan AnnounceInterval { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Clients must not re-announce more often than this.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan MinAnnounceInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Peers that haven't announced for this long are dropped. Should be about twice <see cref="AnnounceInterval"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:10", "2.00:00:00")]
    public TimeSpan PeerTimeout { get; set; } = TimeSpan.FromMinutes(65);

    /// <summary>How often expired peers are pruned from memory.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Upper bound for the client's <c>numwant</c>.</summary>
    [Range(0, 1000)]
    public int MaxNumWant { get; set; } = 200;

    /// <summary>Peers returned when the client doesn't send <c>numwant</c>.</summary>
    [Range(0, 1000)]
    public int DefaultNumWant { get; set; } = 50;

    /// <summary>Peers kept per torrent; the least recently announced are evicted beyond this.</summary>
    [Range(1, 1_000_000)]
    public int MaxPeersPerTorrent { get; set; } = 5000;

    /// <summary>Maximum info hashes answered by a single scrape request.</summary>
    [Range(1, 1000)]
    public int MaxScrapeInfoHashes { get; set; } = 100;

    /// <summary>How often seeders/leechers/completions are written to the database.</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "1.00:00:00")]
    public TimeSpan StatsFlushInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the in-memory whitelist is fully reloaded from the database (self-healing).</summary>
    [Range(typeof(TimeSpan), "00:00:01", "1.00:00:00")]
    public TimeSpan WhitelistRefreshInterval { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often queued <c>TrackerWhitelistEvent</c> rows are applied (and deleted).</summary>
    [Range(typeof(TimeSpan), "00:00:00.050", "1.00:00:00")]
    public TimeSpan WhitelistEventPollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Apply and delete queued <c>TrackerWhitelistEvent</c> rows. Turn off if an external tracker still consumes that queue;
    /// the whitelist then relies on database lookups for unknown hashes and on periodic reloads.
    /// </summary>
    public bool ConsumeWhitelistEvents { get; set; } = true;

    /// <summary>How long an info hash that isn't in the database is remembered as unknown before it is looked up again.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "1.00:00:00")]
    public TimeSpan UnregisteredCacheDuration { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Track any info hash, not just torrents in the database. Only stats of known torrents are persisted.</summary>
    public bool AllowUnregisteredTorrents { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinAnnounceInterval > AnnounceInterval)
        {
            yield return new ValidationResult(
                $"{nameof(MinAnnounceInterval)} must not exceed {nameof(AnnounceInterval)}.",
                [nameof(MinAnnounceInterval)]);
        }

        if (PeerTimeout <= AnnounceInterval)
        {
            yield return new ValidationResult(
                $"{nameof(PeerTimeout)} must be longer than {nameof(AnnounceInterval)}.",
                [nameof(PeerTimeout)]);
        }

        if (DefaultNumWant > MaxNumWant)
        {
            yield return new ValidationResult(
                $"{nameof(DefaultNumWant)} must not exceed {nameof(MaxNumWant)}.",
                [nameof(DefaultNumWant)]);
        }

        if (!string.IsNullOrWhiteSpace(AnnounceUrl)
            && !(Uri.TryCreate(AnnounceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            yield return new ValidationResult(
                $"{nameof(AnnounceUrl)} must be an absolute http or https URL.",
                [nameof(AnnounceUrl)]);
        }
    }
}
