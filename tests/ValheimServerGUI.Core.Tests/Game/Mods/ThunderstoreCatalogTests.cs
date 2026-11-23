using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game.Mods;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.Mods;

/// <summary>Reading Thunderstore's v1 package list, the disk cache and its fallback, and the download host check.</summary>
public sealed class ThunderstoreCatalogTests : IDisposable
{
    private const string ListJson = """
        [
          {
            "name": "ServerBackup", "full_name": "Owner-ServerBackup", "owner": "Owner",
            "package_url": "https://thunderstore.io/c/valheim/p/Owner/ServerBackup/",
            "date_updated": "2026-05-01T10:00:00Z", "rating_score": 12, "is_pinned": false, "is_deprecated": false,
            "has_nsfw_content": false, "categories": ["Server-side", "Utility"],
            "versions": [
              { "name": "ServerBackup", "version_number": "1.1.0", "description": "Backs up the world.",
                "dependencies": ["denikson-BepInExPack_Valheim-5.4.2202"],
                "download_url": "https://thunderstore.io/package/download/Owner/ServerBackup/1.1.0/",
                "downloads": 30, "file_size": 1234, "is_active": true, "icon": "https://gcdn.thunderstore.io/icon.png" },
              { "name": "ServerBackup", "version_number": "1.0.0", "description": "Old.", "dependencies": [],
                "download_url": "https://thunderstore.io/package/download/Owner/ServerBackup/1.0.0/",
                "downloads": 70, "file_size": 1000, "is_active": true }
            ]
          },
          { "name": "NoVersions", "owner": "Owner", "categories": [], "versions": [] }
        ]
        """;

    private readonly string _root = Path.Join(Path.GetTempPath(), "vsg_ts_" + Guid.NewGuid().ToString("N"));
    private readonly StubHttpClientProvider _http = new();
    private DateTime _now = new(2026, 11, 6, 8, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private ThunderstoreCatalog Create() => new(Path.Join(_root, "cache.json"), _http, new FakeApplicationLogger(), () => _now);

    [Fact]
    public void Parse_TakesTheNewestVersion_AndSumsDownloads()
    {
        var packages = ThunderstoreCatalog.ParsePackageList(new MemoryStream(Encoding.UTF8.GetBytes(ListJson)));

        var package = Assert.Single(packages);
        Assert.Equal("Owner-ServerBackup", package.FullName);
        Assert.Equal("1.1.0", package.Latest.VersionNumber);
        Assert.Equal(100, package.TotalDownloads);
        Assert.Equal(new[] { "Server-side", "Utility" }, package.Categories);
        Assert.Equal(new[] { "denikson-BepInExPack_Valheim-5.4.2202" }, package.Latest.Dependencies);
    }

    [Fact]
    public async Task GetAsync_UsesTheSavedList_WhileItIsRecent()
    {
        _http.RespondWith(HttpStatusCode.OK, ListJson);
        await Create().GetAsync(false, CancellationToken.None);
        Assert.Single(_http.Requests);

        _now = _now.AddHours(1);
        var again = await Create().GetAsync(false, CancellationToken.None);
        Assert.Single(_http.Requests);
        Assert.Equal("Owner-ServerBackup", Assert.Single(again.Packages).FullName);
    }

    [Fact]
    public async Task GetAsync_FallsBackToTheSavedList_WhenThunderstoreIsDown()
    {
        _http.RespondWith(HttpStatusCode.OK, ListJson);
        await Create().GetAsync(false, CancellationToken.None);

        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        var snapshot = await Create().GetAsync(forceRefresh: true, CancellationToken.None);

        Assert.True(snapshot.IsStale);
        Assert.Single(snapshot.Packages);
    }

    [Fact]
    public async Task GetAsync_Throws_WhenThunderstoreIsDownAndNothingIsSaved()
    {
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await Assert.ThrowsAsync<HttpRequestException>(() => Create().GetAsync(false, CancellationToken.None));
    }

    [Theory]
    [InlineData("https://thunderstore.io/package/download/a/b/1.0.0/", true)]
    [InlineData("https://gcdn.thunderstore.io/live/repository/packages/a-b-1.0.0.zip", true)]
    [InlineData("http://thunderstore.io/package/download/a/b/1.0.0/", false)]
    [InlineData("https://thunderstore.io.evil.example/x.zip", false)]
    [InlineData("https://example.com/x.zip", false)]
    public void Downloads_OnlyComeFromThunderstore(string url, bool allowed)
        => Assert.Equal(allowed, ThunderstoreCatalog.IsThunderstoreUrl(url));
}
