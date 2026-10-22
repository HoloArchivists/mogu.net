using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Web.Tracking;
using static Mogu.Tests.Tracker.TrackerTestBencode;

namespace Mogu.Tests.Tracker;

[TestClass]
public sealed class TrackerIntegrationTests
{
    private static readonly byte[] KnownHash = [.. Enumerable.Range(0, 20).Select(i => (byte)(0xF0 - i))];

    [TestMethod]
    public async Task AnnounceTracksPeersAndFlushesStatsToDatabase()
    {
        await using var factory = new TrackerWebApplicationFactory();
        var torrentId = await factory.SeedTorrentAsync(KnownHash);
        using var client = factory.CreateClient();

        // Leecher, then a seeder that reports completion; both via X-Forwarded-For from the trusted loopback proxy.
        var leecher = await AnnounceAsync(client, KnownHash, Peer(1), "198.51.100.1", left: 100, "&event=started");
        Assert.AreEqual(1L, leecher["incomplete"]);
        Assert.AreEqual(1800L, leecher["interval"]);
        Assert.IsEmpty((byte[])leecher["peers"]);

        await AnnounceAsync(client, KnownHash, Peer(2), "198.51.100.2", left: 50, "&event=started");
        var completed = await AnnounceAsync(client, KnownHash, Peer(2), "198.51.100.2", left: 0, "&event=completed");
        Assert.AreEqual(1L, completed["complete"]);
        Assert.AreEqual(1L, completed["incomplete"]);
        CollectionAssert.AreEqual(new byte[] { 198, 51, 100, 1, 0x1A, 0xE1 }, (byte[])completed["peers"]);

        var ipv6 = await AnnounceAsync(client, KnownHash, Peer(3), "2001:db8::3", left: 10, "&numwant=5");
        Assert.AreEqual(2L, ipv6["incomplete"]);
        Assert.AreEqual(12, ((byte[])ipv6["peers"]).Length);

        var leecherAgain = await AnnounceAsync(client, KnownHash, Peer(1), "198.51.100.1", left: 100, "");
        CollectionAssert.AreEqual(
            new byte[] { 0x20, 0x01, 0x0D, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 3, 0x1A, 0xE1 },
            (byte[])leecherAgain["peers6"]);

        await AnnounceAsync(client, KnownHash, Peer(3), "2001:db8::3", left: 10, "&event=stopped");

        var torrent = await WaitForAsync(factory, torrentId, t => t.SeedCount == 1 && t.LeechCount == 1 && t.DownloadCount == 1);
        Assert.IsNotNull(torrent.StatsUpdatedAt);

        // Scrape reports DB completions plus the live swarm; unknown hashes are left out.
        byte[] unknownHash = [.. Enumerable.Repeat((byte)0x11, 20)];
        var scrapeBody = await client.GetByteArrayAsync($"/scrape?info_hash={PercentEncode(KnownHash)}&info_hash={PercentEncode(unknownHash)}");
        var files = (Dictionary<string, object>)DecodeDictionary(scrapeBody)["files"];
        var stats = (Dictionary<string, object>)files.Single().Value;
        Assert.AreEqual(System.Text.Encoding.Latin1.GetString(KnownHash), files.Single().Key);
        Assert.AreEqual(1L, stats["complete"]);
        Assert.AreEqual(1L, stats["incomplete"]);
        Assert.AreEqual(1L, stats["downloaded"]);
    }

    [TestMethod]
    public async Task AnnounceRejectsUnregisteredAndBannedTorrents()
    {
        await using var factory = new TrackerWebApplicationFactory();
        var torrentId = await factory.SeedTorrentAsync(KnownHash);
        using var client = factory.CreateClient();

        byte[] unknownHash = [.. Enumerable.Repeat((byte)0x22, 20)];
        var unknown = await AnnounceAsync(client, unknownHash, Peer(1), "198.51.100.1", left: 0, "");
        Assert.AreEqual(TrackerEndpoints.UnregisteredTorrent, Text(unknown["failure reason"]));

        var malformed = DecodeDictionary(await client.GetByteArrayAsync("/announce?info_hash=abc"));
        Assert.AreEqual("Invalid info_hash", Text(malformed["failure reason"]));

        Assert.IsFalse((await AnnounceAsync(client, KnownHash, Peer(1), "198.51.100.1", left: 0, "")).ContainsKey("failure reason"));

        // Banning queues a whitelist "remove" event, which the tracker applies within a poll interval.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
            await db.Torrents.Where(t => t.Id == torrentId).ExecuteUpdateAsync(s => s.SetProperty(t => t.IsBanned, true));
            db.TrackerWhitelistEvents.Add(new TrackerWhitelistEvent { InfoHash = KnownHash, Action = TrackerWhitelistAction.Remove });
            await db.SaveChangesAsync();
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        Dictionary<string, object> banned;
        do
        {
            await Task.Delay(50);
            banned = await AnnounceAsync(client, KnownHash, Peer(1), "198.51.100.1", left: 0, "");
        }
        while (!banned.ContainsKey("failure reason") && DateTime.UtcNow < deadline);

        Assert.AreEqual(TrackerEndpoints.UnregisteredTorrent, Text(banned["failure reason"]));

        // Applied events are deleted from the queue.
        bool pending;
        do
        {
            using var scope = factory.Services.CreateScope();
            pending = await scope.ServiceProvider.GetRequiredService<MoguDbContext>().TrackerWhitelistEvents.AnyAsync();
            if (pending)
            {
                await Task.Delay(50);
            }
        }
        while (pending && DateTime.UtcNow < deadline);

        Assert.IsFalse(pending);
    }

    [TestMethod]
    public async Task ResetInactiveZeroesCountsLeftOverFromBeforeRestart()
    {
        await using var factory = new TrackerWebApplicationFactory();
        byte[] staleHash = [.. Enumerable.Repeat((byte)0x33, 20)];
        var activeId = await factory.SeedTorrentAsync(KnownHash);
        var staleId = await factory.SeedTorrentAsync(staleHash);
        using var client = factory.CreateClient();
        await AnnounceAsync(client, KnownHash, Peer(1), "198.51.100.1", left: 0, "");
        await WaitForAsync(factory, activeId, t => t.SeedCount == 1);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
        await db.Torrents.Where(t => t.Id == staleId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.SeedCount, 7).SetProperty(t => t.LeechCount, 3));

        Assert.AreEqual(1, await factory.Services.GetRequiredService<TrackerStatsWriter>().ResetInactiveAsync(CancellationToken.None));

        var stale = await db.Torrents.AsNoTracking().SingleAsync(t => t.Id == staleId);
        Assert.AreEqual(0, stale.SeedCount);
        Assert.AreEqual(0, stale.LeechCount);
        Assert.AreEqual(1, (await db.Torrents.AsNoTracking().SingleAsync(t => t.Id == activeId)).SeedCount);
    }

    private static async Task<Dictionary<string, object>> AnnounceAsync(
        HttpClient client, byte[] infoHash, byte[] peerId, string address, long left, string extra)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/announce?info_hash={PercentEncode(infoHash)}&peer_id={PercentEncode(peerId)}&port=6881&uploaded=0&downloaded=0&left={left}&compact=1{extra}");
        request.Headers.Add("X-Forwarded-For", address);
        using var response = await client.SendAsync(request);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/plain", response.Content.Headers.ContentType?.MediaType);
        return DecodeDictionary(await response.Content.ReadAsByteArrayAsync());
    }

    private static async Task<Torrent> WaitForAsync(TrackerWebApplicationFactory factory, int torrentId, Func<Torrent, bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            using var scope = factory.Services.CreateScope();
            var torrent = await scope.ServiceProvider.GetRequiredService<MoguDbContext>()
                .Torrents.AsNoTracking().SingleAsync(t => t.Id == torrentId);
            if (condition(torrent) || DateTime.UtcNow > deadline)
            {
                Assert.IsTrue(condition(torrent), $"Stats not flushed: {torrent.SeedCount}/{torrent.LeechCount}/{torrent.DownloadCount}");
                return torrent;
            }

            await Task.Delay(50);
        }
    }

    private static byte[] Peer(int n) => [.. Enumerable.Repeat((byte)('A' + n), 20)];
}

/// <summary>Runs the app against a throwaway SQLite database with fast tracker timers.</summary>
/// <remarks>
/// Any Mogu.Web type identifies the entry assembly; <c>Program</c> itself is ambiguous because
/// Mogu.LegacyImport (also referenced by the tests) has a top-level <c>Program</c> too.
/// </remarks>
internal sealed class TrackerWebApplicationFactory : WebApplicationFactory<TrackerWhitelist>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mogu-tracker-tests-" + Guid.NewGuid().ToString("N"));

    public async Task<int> SeedTorrentAsync(byte[] infoHash)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
        var torrent = new Torrent
        {
            InfoHash = infoHash,
            DisplayName = "Test torrent",
            TorrentName = "test.torrent",
            MainCategoryId = 1,
            SubCategoryId = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Torrents.Add(torrent);
        await db.SaveChangesAsync();
        return torrent.Id;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(directory);
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Mogu", $"Data Source={Path.Combine(directory, "mogu.db")}");
        builder.UseSetting("Mogu:DataDirectory", directory);
        builder.UseSetting("Tracker:StatsFlushInterval", "00:00:00.100");
        builder.UseSetting("Tracker:WhitelistEventPollInterval", "00:00:00.100");
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, LoopbackRemoteAddressFilter>());
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // TestServer leaves RemoteIpAddress unset; pretend requests come from the local reverse proxy.
    private sealed class LoopbackRemoteAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    return nextMiddleware(context);
                });
                next(app);
            };
    }
}
