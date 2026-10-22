using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Options;
using Mogu.Web.Services;

namespace Mogu.Tests.Browsing;

[TestClass]
public sealed class TorrentSearchTests : IDisposable
{
    private TempDatabase database = null!;
    private MoguDbContext db = null!;
    private MoguOptions options = null!;
    private User alice = null!;
    private User bob = null!;
    private User moderator = null!;

    [TestInitialize]
    public async Task Initialize()
    {
        database = new TempDatabase();
        db = await database.CreateMigratedContextAsync();
        options = new MoguOptions { ResultsPerPage = 2, MaxPages = 3 };

        alice = Seed.User("alice");
        bob = Seed.User("bob", UserLevel.Trusted);
        moderator = Seed.User("mod", UserLevel.Moderator);
        db.Users.AddRange(alice, bob, moderator);
        db.Torrents.AddRange(
            Seed.Torrent("alice public", alice, size: 300, seeders: 5),
            Seed.Torrent("alice hidden", alice, hidden: true),
            Seed.Torrent("alice anon", alice, anonymous: true),
            Seed.Torrent("alice deleted", alice, deleted: true),
            Seed.Torrent("bob public", bob, main: 2, sub: 1, trusted: true, size: 100, seeders: 5),
            Seed.Torrent("bob remake", bob, main: 2, sub: 3, remake: true, complete: true, size: 200, seeders: 9));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    public void Dispose()
    {
        db.Dispose();
        database.Dispose();
    }

    private static void AssertNames(string expected, IEnumerable<string> actual) =>
        Assert.AreEqual(expected, string.Join("|", actual));

    private async Task<TorrentSearchResult> SearchAsync(TorrentSearchRequest request)
    {
        var result = await new TorrentSearch(db, new FixedOptions<MoguOptions>(options)).SearchAsync(request);
        return result;
    }

    private async Task<string[]> NamesAsync(TorrentSearchRequest request)
    {
        var result = await SearchAsync(request);
        Assert.IsTrue(result.Succeeded, $"Search failed with {result.StatusCode}");
        return result.Torrents.Items.Select(t => t.DisplayName).ToArray();
    }

    private async Task<string[]> AllNamesAsync(TorrentSearchRequest request)
    {
        options.ResultsPerPage = 100;
        var names = await NamesAsync(request);
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    [TestMethod]
    public async Task AnonymousSeesOnlyPublicNotDeleted()
    {
        var names = await AllNamesAsync(new TorrentSearchRequest());
        AssertNames("alice anon|alice public|bob public|bob remake", names);
    }

    [TestMethod]
    public async Task LoggedInSeesOwnHiddenTorrentsButNotInRss()
    {
        CollectionAssert.Contains(await AllNamesAsync(new TorrentSearchRequest { Viewer = alice }), "alice hidden");
        CollectionAssert.DoesNotContain(await AllNamesAsync(new TorrentSearchRequest { Viewer = bob }), "alice hidden");
        CollectionAssert.DoesNotContain(await AllNamesAsync(new TorrentSearchRequest { Viewer = alice, Rss = true }), "alice hidden");
    }

    [TestMethod]
    public async Task ModeratorSeesEverything()
    {
        var names = await AllNamesAsync(new TorrentSearchRequest { Viewer = moderator });
        Assert.HasCount(6, names);
    }

    [TestMethod]
    public async Task UserViewOthersSeeNoHiddenOrAnonymous()
    {
        AssertNames("alice public", await AllNamesAsync(new TorrentSearchRequest { UserId = alice.Id }));
        AssertNames("alice public", await AllNamesAsync(new TorrentSearchRequest { UserId = alice.Id, Viewer = bob }));
    }

    [TestMethod]
    public async Task UserViewSelfSeesHiddenAndAnonymousExceptInRss()
    {
        AssertNames("alice anon|alice hidden|alice public", await AllNamesAsync(new TorrentSearchRequest { UserId = alice.Id, Viewer = alice }));
        AssertNames("alice public", await AllNamesAsync(new TorrentSearchRequest { UserId = alice.Id, Viewer = alice, Rss = true }));
    }

    [TestMethod]
    public async Task UserViewModeratorSeesDeleted()
    {
        Assert.HasCount(4, await AllNamesAsync(new TorrentSearchRequest { UserId = alice.Id, Viewer = moderator }));
    }

    [TestMethod]
    [DataRow("1", new[] { "alice anon", "alice public", "bob public" })]
    [DataRow("2", new[] { "bob public" })]
    [DataRow("3", new[] { "bob remake" })]
    [DataRow("0", new[] { "alice anon", "alice public", "bob public", "bob remake" })]
    public async Task QualityFilters(string filter, string[] expected) =>
        CollectionAssert.AreEqual(expected, await AllNamesAsync(new TorrentSearchRequest { QualityFilter = filter }));

    [TestMethod]
    [DataRow("2_0", new[] { "bob public", "bob remake" })]
    [DataRow("2_3", new[] { "bob remake" })]
    [DataRow("1_1", new[] { "alice anon", "alice public" })]
    [DataRow("0_0", new[] { "alice anon", "alice public", "bob public", "bob remake" })]
    [DataRow("9_0", new[] { "alice anon", "alice public", "bob public", "bob remake" })]
    [DataRow("2_99", new[] { "alice anon", "alice public", "bob public", "bob remake" })]
    public async Task Categories(string category, string[] expected) =>
        CollectionAssert.AreEqual(expected, await AllNamesAsync(new TorrentSearchRequest { Category = category }));

    [TestMethod]
    [DataRow("bad", "desc", "0", "0_0")]
    [DataRow("id", "sideways", "0", "0_0")]
    [DataRow("id", "desc", "4", "0_0")]
    [DataRow("id", "desc", "0", "1")]
    [DataRow("id", "desc", "0", "a_b")]
    [DataRow("name", "desc", "0", "0_0")]
    public async Task InvalidParametersAre400(string sort, string order, string filter, string category)
    {
        var result = await SearchAsync(new TorrentSearchRequest { Sort = sort, Order = order, QualityFilter = filter, Category = category });
        Assert.AreEqual(400, result.StatusCode);
    }

    [TestMethod]
    public async Task SortAndOrderAreCaseInsensitive()
    {
        var result = await SearchAsync(new TorrentSearchRequest { Sort = "SIZE", Order = "ASC" });
        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    public async Task SortBySize()
    {
        AssertNames("bob public|bob remake|alice public|alice anon", await NamesAsyncAll(new TorrentSearchRequest { Sort = "size", Order = "asc" }));
    }

    [TestMethod]
    public async Task SortTiesBrokenByIdInSameDirection()
    {
        // "alice public" and "bob public" both have 5 seeders; alice's is older.
        AssertNames("bob remake|bob public|alice public|alice anon", await NamesAsyncAll(new TorrentSearchRequest { Sort = "seeders", Order = "desc" }));
        AssertNames("alice anon|alice public|bob public|bob remake", await NamesAsyncAll(new TorrentSearchRequest { Sort = "seeders", Order = "asc" }));
    }

    [TestMethod]
    public async Task DefaultSortIsNewestFirst()
    {
        AssertNames("bob remake|bob public|alice anon|alice public", await NamesAsyncAll(new TorrentSearchRequest()));
    }

    [TestMethod]
    public async Task RssIgnoresSortAndPage()
    {
        options.ResultsPerPage = 2;
        var result = await SearchAsync(new TorrentSearchRequest { Rss = true, Sort = "size", Order = "asc", Page = 50 });

        // Page 50 is past MaxPages, which still applies to RSS like in the old code.
        Assert.AreEqual(403, result.StatusCode);

        var names = await NamesAsync(new TorrentSearchRequest { Rss = true, Sort = "size", Order = "asc" });
        AssertNames("bob remake|bob public", names);
    }

    [TestMethod]
    [DataRow("alice", new[] { "alice anon", "alice public" })]
    [DataRow("ALICE PUBLIC", new[] { "alice public" })]
    [DataRow("\"bob public\"", new[] { "bob public" })]
    [DataRow("\"public bob\"", new string[0])]
    [DataRow("public bob", new[] { "bob public" })]
    [DataRow("bob -remake", new[] { "bob public" })]
    [DataRow("anon|remake", new[] { "alice anon", "bob remake" })]
    [DataRow("-alice", new[] { "bob public", "bob remake" })]
    [DataRow("a b", new[] { "alice anon", "alice public", "bob public", "bob remake" })]
    public async Task TermSearch(string term, string[] expected) =>
        CollectionAssert.AreEqual(expected, await AllNamesAsync(new TorrentSearchRequest { Term = term }));

    [TestMethod]
    public async Task TermSearchTreatsLikeWildcardsLiterally()
    {
        db.Torrents.AddRange(Seed.Torrent("Songs 100% live"), Seed.Torrent("Songs 1000 live"), Seed.Torrent("a_b"), Seed.Torrent("axb"));
        await db.SaveChangesAsync();

        AssertNames("Songs 100% live", await AllNamesAsync(new TorrentSearchRequest { Term = "100%" }));
        AssertNames("a_b", await AllNamesAsync(new TorrentSearchRequest { Term = "a_b" }));
    }

    [TestMethod]
    public async Task PagingCountsAndPages()
    {
        var result = await SearchAsync(new TorrentSearchRequest { Page = 2 });
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(4, result.ActualCount);
        Assert.AreEqual(2, result.Torrents.PageCount);
        AssertNames("alice anon|alice public", result.Torrents.Items.Select(t => t.DisplayName).ToArray());
    }

    [TestMethod]
    public async Task EmptyPageAfterFirstIs404()
    {
        Assert.AreEqual(404, (await SearchAsync(new TorrentSearchRequest { Page = 3 })).StatusCode);
        Assert.IsTrue((await SearchAsync(new TorrentSearchRequest { Term = "nothing matches" })).Succeeded);
        Assert.AreEqual(404, (await SearchAsync(new TorrentSearchRequest { Page = (long)uint.MaxValue + 1, Viewer = moderator })).StatusCode);
    }

    [TestMethod]
    public async Task MaxPagesIs403ExceptForModeratorsAndOwnListing()
    {
        var result = await SearchAsync(new TorrentSearchRequest { Page = 4 });
        Assert.AreEqual(403, result.StatusCode);
        Assert.AreEqual(TorrentSearch.MaxPagesMessage, result.Message);

        // Exempt users get past the limit (and then a 404 because the page is empty).
        Assert.AreEqual(404, (await SearchAsync(new TorrentSearchRequest { Page = 4, Viewer = moderator })).StatusCode);
        Assert.AreEqual(404, (await SearchAsync(new TorrentSearchRequest { Page = 4, Viewer = alice, UserId = alice.Id })).StatusCode);
        Assert.AreEqual(403, (await SearchAsync(new TorrentSearchRequest { Page = 4, Viewer = alice, UserId = bob.Id })).StatusCode);
    }

    [TestMethod]
    public async Task TotalCountIsCappedByMaxPages()
    {
        options.MaxPages = 1;
        var result = await SearchAsync(new TorrentSearchRequest());
        Assert.AreEqual(4, result.ActualCount);
        Assert.AreEqual(2, result.Torrents.TotalCount);
        Assert.IsFalse(result.Torrents.HasNext);
    }

    [TestMethod]
    public async Task RowsCarryListingFields()
    {
        options.ResultsPerPage = 100;
        var result = await SearchAsync(new TorrentSearchRequest { Term = "\"bob remake\"" });
        var row = result.Torrents.Items.Single();
        Assert.AreEqual("2_3", row.CategoryCode);
        Assert.AreEqual("danger", row.RowClass);
        Assert.AreEqual(9, row.SeedCount);
        Assert.AreEqual(40, row.InfoHashHex.Length);
        Assert.IsTrue(row.HasTorrent);
    }

    private async Task<string[]> NamesAsyncAll(TorrentSearchRequest request)
    {
        options.ResultsPerPage = 100;
        return await NamesAsync(request);
    }
}
