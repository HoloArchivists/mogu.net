using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Mogu.Core.Data;
using Mogu.Core.Entities;
using Mogu.Core.Torrents;
using Mogu.Web.Api;
using Mogu.Web.Infrastructure;
using static Mogu.Tests.Torrents.TestTorrents;

namespace Mogu.Tests.Torrents;

[TestClass]
public sealed class ApiIntegrationTests
{
    private string _directory = null!;
    // Any Mogu.Web type identifies the app's assembly; Program itself is ambiguous because
    // Mogu.LegacyImport has top-level statements too.
    private WebApplicationFactory<CurrentUser> _factory = null!;

    [TestInitialize]
    public void Initialize()
    {
        _directory = Path.Combine(Path.GetTempPath(), "mogu-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _factory = new WebApplicationFactory<CurrentUser>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Mogu", $"Data Source={Path.Combine(_directory, "mogu.db")};Pooling=False");
            builder.UseSetting("Mogu:DataDirectory", Path.Combine(_directory, "data"));
            builder.UseSetting("Mogu:MainAnnounceUrl", MainTracker);
        });
    }

    [TestCleanup]
    public async Task CleanupAsync()
    {
        await _factory.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private async Task<string> CreateUserWithTokenAsync(string username)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoguDbContext>();
        var user = new User { Username = username, DiscordId = 42, CreatedAt = DateTime.UtcNow.AddDays(-30) };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return await scope.ServiceProvider.GetRequiredService<ApiTokenService>().GenerateAsync(user, CancellationToken.None);
    }

    private static MultipartFormDataContent UploadContent(byte[] torrent, string json)
    {
        var file = new ByteArrayContent(torrent);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-bittorrent");
        return new MultipartFormDataContent
        {
            { file, "torrent", "show.torrent" },
            { new StringContent(json), "torrent_data" },
        };
    }

    private static AuthenticationHeaderValue Basic(string username, string password) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));

    [TestMethod]
    public async Task UploadWithTokenThenDownload()
    {
        var token = await CreateUserWithTokenAsync("uploader");
        using var client = _factory.CreateClient();
        var torrent = Encode(MultiFile("API Show", ("ep01.mkv", 5_000_000)));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/upload")
        {
            Content = UploadContent(torrent, """{"category": "2_1", "name": "Nice name", "hidden": true}"""),
        };
        request.Headers.Authorization = Basic("uploader", token);

        using var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, body);
        using var json = JsonDocument.Parse(body);
        var id = json.RootElement.GetProperty("id").GetInt32();
        var hash = json.RootElement.GetProperty("hash").GetString();
        Assert.AreEqual("Nice name", json.RootElement.GetProperty("name").GetString());
        Assert.AreEqual($"http://localhost/view/{id}", json.RootElement.GetProperty("url").GetString());
        Assert.StartsWith($"magnet:?xt=urn:btih:{hash}&dn=Nice%20name&tr=", json.RootElement.GetProperty("magnet").GetString()!);

        using var download = await client.GetAsync($"/download/{id}.torrent");
        Assert.AreEqual(HttpStatusCode.OK, download.StatusCode);
        Assert.AreEqual("application/x-bittorrent", download.Content.Headers.ContentType!.MediaType);
        Assert.AreEqual("inline; filename=\"show.torrent\"; filename*=UTF-8''show.torrent", download.Content.Headers.GetValues("Content-Disposition").Single());
        var file = TorrentMetadata.Parse(await download.Content.ReadAsByteArrayAsync());
        Assert.AreEqual(hash, file.InfoHashHex);

        using var info = new HttpRequestMessage(HttpMethod.Get, $"/api/info/{hash}");
        info.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var infoResponse = await client.SendAsync(info);
        using var infoJson = JsonDocument.Parse(await infoResponse.Content.ReadAsStringAsync());
        Assert.AreEqual(id, infoJson.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual("uploader", infoJson.RootElement.GetProperty("submitter").GetString());
        Assert.AreEqual(5_000_000, infoJson.RootElement.GetProperty("files").GetProperty("API Show").GetProperty("ep01.mkv").GetInt64());

        using var magnet = await _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false }).GetAsync($"/view/{id}/magnet");
        Assert.AreEqual(HttpStatusCode.Redirect, magnet.StatusCode);
        Assert.StartsWith("magnet:?xt=urn:btih:" + hash, magnet.Headers.Location!.OriginalString);
    }

    [TestMethod]
    public async Task UploadMapsErrorsToApiKeys()
    {
        var token = await CreateUserWithTokenAsync("uploader");
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.PostAsync("/api/upload", UploadContent("nope"u8.ToArray(), """{"name": "ab"}"""));

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = json.RootElement.GetProperty("errors");
        Assert.AreEqual("Malformed torrent file", errors.GetProperty("torrent")[0].GetString());
        Assert.AreEqual("Torrent display name must be at least 3 characters long and 255 at most.", errors.GetProperty("name")[0].GetString());
        Assert.AreEqual("Please select a category", errors.GetProperty("category")[0].GetString());
    }

    [TestMethod]
    public async Task UploadRejectsBadOrMissingTokens()
    {
        var token = await CreateUserWithTokenAsync("uploader");
        using var client = _factory.CreateClient();

        foreach (var authorization in new AuthenticationHeaderValue?[]
        {
            null,
            Basic("uploader", "mogu_wrong"),
            Basic("someone-else", token),
            new("Bearer", token + "x"),
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/upload")
            {
                Content = UploadContent(Encode(SingleFile("a", 1)), "{}"),
            };
            request.Headers.Authorization = authorization;

            using var response = await client.SendAsync(request);

            Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.AreEqual("""{"errors":["Bad authorization"]}""", await response.Content.ReadAsStringAsync());
        }
    }

    [TestMethod]
    public async Task UploadRequiresTorrentData()
    {
        var token = await CreateUserWithTokenAsync("uploader");
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var missing = await client.PostAsync("/api/v2/upload", new MultipartFormDataContent { { new StringContent("x"), "other" } });
        using var invalid = await client.PostAsync("/api/v2/upload", UploadContent([], "{nope"));

        Assert.AreEqual("""{"errors":["missing torrent_data field"]}""", await missing.Content.ReadAsStringAsync());
        Assert.AreEqual("""{"errors":["unable to parse valid JSON in torrent_data"]}""", await invalid.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task DownloadOfUnknownTorrentIsNotFound()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/download/12345.torrent");

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task UploadPageRendersAndRedirectsAfterUpload()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using (var login = await client.GetAsync("/dev/login/pageuser?level=Trusted"))
        {
            Assert.AreEqual(HttpStatusCode.Redirect, login.StatusCode);
        }

        var page = await client.GetStringAsync("/upload");
        Assert.Contains("<option disabled=\"disabled\" value=\"1_0\">Unarchived</option>", page);
        Assert.Contains("id=\"is_trusted\"", page);
        var token = System.Text.RegularExpressions.Regex.Match(page, "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"").Groups[1].Value;

        var file = new ByteArrayContent(Encode(MultiFile("Page upload", ("a.mkv", 1))));
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { file, "torrent_file", "page.torrent" },
            { new StringContent("1_2"), "category" },
            { new StringContent("true"), "is_trusted" },
        };

        using var response = await client.PostAsync("/upload", form);

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, await response.Content.ReadAsStringAsync());
        Assert.AreEqual("/view/1", response.Headers.Location!.OriginalString);
    }
}
