using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mogu.Core.Data;
using Mogu.Core.Entities;

namespace Mogu.Tests.Torrents;

/// <summary>The web app on a temporary SQLite database, with helpers for dev logins and antiforgery tokens.</summary>
// Any type from Mogu.Web marks the entry assembly; "Program" is ambiguous with the LegacyImport tool.
internal sealed partial class TorrentSiteFactory : WebApplicationFactory<Mogu.Web.Infrastructure.CurrentUser>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mogu-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(directory);
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Mogu", $"Data Source={Path.Combine(directory, "mogu.db")}");
        builder.UseSetting("Mogu:DataDirectory", directory);
        builder.UseSetting("Mogu:EditingTimeLimit", "00:30:00");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A client logged in through <c>/dev/login</c>, or anonymous when <paramref name="username"/> is null.</summary>
    public async Task<HttpClient> ClientAsync(string? username = null, UserLevel level = UserLevel.Regular)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (username is not null)
        {
            using var response = await client.GetAsync(new Uri($"/dev/login/{username}?level={level}", UriKind.Relative));
            Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        }

        return client;
    }

    public async Task<T> WithDbAsync<T>(Func<MoguDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<MoguDbContext>());
    }

    public Task<int> UserIdAsync(string username) =>
        WithDbAsync(db => db.Users.Where(u => u.Username == username).Select(u => u.Id).SingleAsync());

    public Task<Torrent> AddTorrentAsync(string? uploader, Action<Torrent>? configure = null) => WithDbAsync(async db =>
    {
        var torrent = new Torrent
        {
            InfoHash = Guid.NewGuid().ToByteArray().Concat(new byte[4]).ToArray(),
            DisplayName = "Integration torrent",
            TorrentName = "integration.torrent",
            UploaderId = uploader is null ? null : await db.Users.Where(u => u.Username == uploader).Select(u => (int?)u.Id).SingleAsync(),
            UploaderIp = "10.1.2.3",
            MainCategoryId = 1,
            SubCategoryId = 2,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        configure?.Invoke(torrent);
        db.Torrents.Add(torrent);
        await db.SaveChangesAsync();
        return torrent;
    });

    /// <summary>GETs <paramref name="url"/> and returns the first antiforgery token on the page.</summary>
    public static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(new Uri(url, UriKind.Relative));
        var match = TokenRegex().Match(html);
        Assert.IsTrue(match.Success, $"No antiforgery token on {url}");
        return match.Groups[1].Value;
    }

    public static Task<HttpResponseMessage> PostAsync(HttpClient client, string url, string token, params (string Key, string Value)[] fields) =>
        client.PostAsync(
            new Uri(url, UriKind.Relative),
            new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Key, f.Value)).Append(KeyValuePair.Create("__RequestVerificationToken", token))));

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();
}
