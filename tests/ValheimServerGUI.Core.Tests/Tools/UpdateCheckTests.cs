using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Serilog;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Http;
using ValheimServerGUI.Tools.Models;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Tools
{
    // The update check asks the app's API (/update-check), which picks the release server-side; the client only
    // interprets the answer: 200 = latest release, 404 = none qualifies, anything else (or an unusable 200) fails.
    public class UpdateCheckTests
    {
        private const string Release =
            "{\"version\":\"v2.4.0\",\"url\":\"https://forge.example/releases/tag/v2.4.0\",\"publishedAt\":\"2026-09-27T19:09:13Z\"}";

        private static readonly RuneberryApiKey ApiKey = new("X-Test-Api-Key", "test-key-value");

        private readonly StubHttpClientProvider _http = new();

        private RuneberryApiClient NewClient()
            => new(new RestClientContext(new LoggerConfiguration().CreateLogger(), _http), ApiKey);

        [Fact]
        public async Task Asks_the_api_update_check_route_with_the_client_key()
        {
            _http.RespondWith(HttpStatusCode.OK, Release);

            await NewClient().GetLatestReleaseAsync();

            var (request, _) = Assert.Single(_http.Requests);
            Assert.Equal($"{CoreConstants.UrlRuneberryApi}/update-check", request.RequestUri!.ToString());
            Assert.Equal(new[] { ApiKey.Value }, request.Headers.GetValues(ApiKey.Header));
        }

        [Fact]
        public async Task A_200_returns_the_version_and_release_page()
        {
            _http.RespondWith(HttpStatusCode.OK, Release);

            var release = await NewClient().GetLatestReleaseAsync();

            Assert.Equal("v2.4.0", release!.Version);
            Assert.Equal("https://forge.example/releases/tag/v2.4.0", release.Url);
        }

        [Fact]
        public async Task A_404_means_no_qualifying_release()
        {
            _http.RespondWith(HttpStatusCode.NotFound, "{\"message\":\"No release\"}");

            Assert.Null(await NewClient().GetLatestReleaseAsync());
        }

        [Theory]
        [InlineData(HttpStatusCode.BadGateway, "{\"message\":\"forge down\"}")]
        [InlineData(HttpStatusCode.Unauthorized, "{\"message\":\"bad key\"}")]
        [InlineData(HttpStatusCode.OK, "not json")]
        [InlineData(HttpStatusCode.OK, "{\"url\":\"https://forge.example/r\"}")]
        [InlineData(HttpStatusCode.OK, "{\"version\":\"not-a-version\",\"url\":\"https://forge.example/r\"}")]
        [InlineData(HttpStatusCode.OK, "{\"version\":\"v2.4.0\"}")]
        public async Task An_error_status_or_unusable_answer_fails_the_check(HttpStatusCode status, string body)
        {
            _http.RespondWith(status, body);

            var e = await Assert.ThrowsAsync<Exception>(() => NewClient().GetLatestReleaseAsync());
            Assert.Equal(Strings.Api_UpdateServerUnreachable, e.Message);
        }

        [Fact]
        public async Task An_unreachable_api_fails_the_check()
        {
            _http.Respond = _ => throw new HttpRequestException("no route");

            var e = await Assert.ThrowsAsync<Exception>(() => NewClient().GetLatestReleaseAsync());
            Assert.Equal(Strings.Api_UpdateServerUnreachable, e.Message);
        }

        // ---- builds without a key: this fork's GitHub releases ----

        private RuneberryApiClient NewKeylessClient()
            => new(new RestClientContext(new LoggerConfiguration().CreateLogger(), _http), new RuneberryApiKey("", ""));

        [Fact]
        public async Task Without_a_key_asks_the_fork_github_releases_without_a_key_header()
        {
            _http.RespondWith(HttpStatusCode.OK, "{\"tag_name\":\"v3.1.0\",\"html_url\":\"https://github.com/o/r/releases/tag/v3.1.0\",\"prerelease\":false}");

            var release = await NewKeylessClient().GetLatestReleaseAsync();

            var (request, _) = Assert.Single(_http.Requests);
            Assert.Equal(CoreConstants.UrlGitHubLatestRelease, request.RequestUri!.ToString());
            Assert.False(request.Headers.Contains(ApiKey.Header));
            Assert.Contains("ValheimServerGUI", request.Headers.UserAgent.ToString());
            Assert.Equal("v3.1.0", release!.Version);
            Assert.Equal("https://github.com/o/r/releases/tag/v3.1.0", release.Url);
        }

        [Fact]
        public async Task Without_a_key_the_release_lists_its_files()
        {
            _http.RespondWith(HttpStatusCode.OK, "{\"tag_name\":\"v3.4.0\",\"html_url\":\"https://github.com/o/r/releases/tag/v3.4.0\","
                + "\"assets\":[{\"name\":\"ValheimServerGUI-3.4.0-win-x64-setup.exe\",\"size\":123,"
                + "\"browser_download_url\":\"https://github.com/o/r/releases/download/v3.4.0/ValheimServerGUI-3.4.0-win-x64-setup.exe\"}]}");

            var release = await NewKeylessClient().GetLatestReleaseAsync();

            var asset = Assert.Single(release!.Assets!);
            Assert.Equal("ValheimServerGUI-3.4.0-win-x64-setup.exe", asset.Name);
            Assert.Equal(123, asset.Size);
            Assert.Equal("https://github.com/o/r/releases/download/v3.4.0/ValheimServerGUI-3.4.0-win-x64-setup.exe", asset.DownloadUrl);
        }

        [Fact]
        public async Task Without_a_key_a_404_means_no_release_yet()
        {
            _http.RespondWith(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}");

            Assert.Null(await NewKeylessClient().GetLatestReleaseAsync());
        }

        [Theory]
        [InlineData(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}")]
        [InlineData(HttpStatusCode.OK, "{\"html_url\":\"https://github.com/o/r\"}")]
        [InlineData(HttpStatusCode.OK, "{\"tag_name\":\"latest\",\"html_url\":\"https://github.com/o/r\"}")]
        public async Task Without_a_key_an_error_or_unusable_release_fails_the_check(HttpStatusCode status, string body)
        {
            _http.RespondWith(status, body);

            var e = await Assert.ThrowsAsync<Exception>(() => NewKeylessClient().GetLatestReleaseAsync());
            Assert.Equal(Strings.Api_UpdateServerUnreachable, e.Message);
        }

        // GitHub answers 403 once the 60 keyless API requests an hour of the user's internet address are spent; the
        // check then reads the same release from the web pages, which have no such limit.
        private void RateLimitedApiWithPages(string tag, HttpStatusCode sumsStatus, string sums)
            => _http.Respond = request =>
            {
                var url = request.RequestUri!.ToString();
                if (url == CoreConstants.UrlGitHubLatestRelease)
                    return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"API rate limit exceeded\"}") };
                if (url == CoreConstants.UrlGitHubLatestReleasePage)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        // The client follows the page's redirect, so the answer's request is the tag's page.
                        RequestMessage = new HttpRequestMessage(HttpMethod.Head, $"{CoreConstants.UrlGitHubReleaseTagPage}/{tag}"),
                    };
                if (url == $"{CoreConstants.UrlGitHubReleaseDownload}/{tag}/SHA256SUMS.txt")
                    return new HttpResponseMessage(sumsStatus) { Content = new StringContent(sums) };
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            };

        [Fact]
        public async Task Without_a_key_a_rate_limited_api_reads_the_release_pages_instead()
        {
            RateLimitedApiWithPages("v0.0.2-alpha", HttpStatusCode.OK,
                "abc  ValheimServerGUI-0.0.2-alpha-win-x64-setup.exe\ndef *ValheimServerGUI-0.0.2-alpha-win-x64-portable.exe\n");

            var release = await NewKeylessClient().GetLatestReleaseAsync();

            Assert.Equal("v0.0.2-alpha", release!.Version);
            Assert.Equal($"{CoreConstants.UrlGitHubReleaseTagPage}/v0.0.2-alpha", release.Url);
            Assert.Equal(
                new[] { "ValheimServerGUI-0.0.2-alpha-win-x64-setup.exe", "ValheimServerGUI-0.0.2-alpha-win-x64-portable.exe", "SHA256SUMS.txt" },
                release.Assets!.Select(a => a.Name));
            Assert.Equal(
                $"{CoreConstants.UrlGitHubReleaseDownload}/v0.0.2-alpha/ValheimServerGUI-0.0.2-alpha-win-x64-setup.exe",
                release.Assets![0].DownloadUrl);
        }

        [Fact]
        public async Task Without_a_key_a_rate_limited_api_still_reports_a_release_whose_files_cannot_be_read()
        {
            RateLimitedApiWithPages("v3.9.0", HttpStatusCode.NotFound, "");

            var release = await NewKeylessClient().GetLatestReleaseAsync();

            Assert.Equal("v3.9.0", release!.Version);
            Assert.Empty(release.Assets!);
        }

        [Theory]
        [InlineData("", new string[0])]
        [InlineData("abc  a.exe\r\n\r\ndef  b.tar.gz", new[] { "a.exe", "b.tar.gz" })]
        [InlineData("abc *a.exe\nnoname\nabc  ../evil.exe", new[] { "a.exe" })]
        public void Reads_the_file_names_from_the_checksums(string checksums, string[] expected)
        {
            Assert.Equal(expected, RuneberryApiClient.ParseChecksumFileNames(checksums));
        }

        // ---- SoftwareUpdateProvider ----

        private static SoftwareUpdateEventArgs Check(FakeRuneberryApiClient api)
        {
            var provider = new SoftwareUpdateProvider(api, new StubUserPreferencesProvider());
            SoftwareUpdateEventArgs? result = null;
            provider.UpdateCheckFinished += (_, e) => result = e;
            provider.CheckForUpdatesAsync(isManualCheck: true).GetAwaiter().GetResult();
            return result!;
        }

        [Fact]
        public void A_release_reports_its_version_and_page()
        {
            var result = Check(new FakeRuneberryApiClient { LatestRelease = new() { Version = "v9.9.9", Url = "https://forge.example/r" } });

            Assert.True(result.IsSuccessful);
            Assert.Equal("v9.9.9", result.LatestVersion);
            Assert.Equal("https://forge.example/r", result.ReleaseUrl);
            Assert.Empty(result.Assets);
        }

        [Fact]
        public void A_release_passes_on_its_files()
        {
            var asset = new ReleaseAsset { Name = "a.exe", DownloadUrl = "https://forge.example/a.exe" };
            var result = Check(new FakeRuneberryApiClient
            {
                LatestRelease = new() { Version = "v9.9.9", Url = "https://forge.example/r", Assets = new() { asset } },
            });

            Assert.Same(asset, Assert.Single(result.Assets));
        }

        [Fact]
        public void No_qualifying_release_reports_the_running_version_without_a_page()
        {
            var result = Check(new FakeRuneberryApiClient { LatestRelease = null });

            Assert.True(result.IsSuccessful);
            Assert.Equal(AssemblyHelper.GetApplicationVersion(), result.LatestVersion);
            Assert.Null(result.ReleaseUrl);
        }

        [Fact]
        public void A_failed_check_reports_the_error_without_a_page()
        {
            var result = Check(new FakeRuneberryApiClient { LatestReleaseError = new Exception("down") });

            Assert.False(result.IsSuccessful);
            Assert.Equal("down", result.Exception!.Message);
            Assert.Null(result.ReleaseUrl);
        }

        // ---- automatic checks: a cheap look at the latest tag, a full check only when it changed ----

        private sealed class Clock
        {
            public DateTime Now = new(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        }

        private static (SoftwareUpdateProvider Provider, List<SoftwareUpdateEventArgs> Results, Clock Clock) NewAutomatic(
            FakeRuneberryApiClient api, bool checkForUpdates = true)
        {
            var clock = new Clock();
            var provider = new SoftwareUpdateProvider(api, new StubUserPreferencesProvider { CheckForUpdates = checkForUpdates })
            {
                UtcNow = () => clock.Now,
            };
            var results = new List<SoftwareUpdateEventArgs>();
            provider.UpdateCheckFinished += (_, e) => results.Add(e);
            return (provider, results, clock);
        }

        [Fact]
        public async Task An_unchanged_tag_skips_the_full_check()
        {
            var api = new FakeRuneberryApiClient { LatestRelease = new() { Version = "v3.6.0", Url = "https://x" }, LatestTag = "v3.6.0" };
            var (provider, results, clock) = NewAutomatic(api);

            await provider.CheckForUpdatesAsync(isManualCheck: false);
            clock.Now += CoreConstants.UpdateCheckInterval;
            await provider.CheckForUpdatesAsync(isManualCheck: false);

            Assert.Single(results);
            Assert.Equal(1, api.TagLooks);
        }

        [Fact]
        public async Task A_new_tag_runs_the_full_check()
        {
            var api = new FakeRuneberryApiClient { LatestRelease = new() { Version = "v3.6.0", Url = "https://x" }, LatestTag = "v3.6.0" };
            var (provider, results, clock) = NewAutomatic(api);
            await provider.CheckForUpdatesAsync(isManualCheck: false);

            api.LatestTag = "v3.6.1";
            api.LatestRelease = new() { Version = "v3.6.1", Url = "https://x" };
            clock.Now += CoreConstants.UpdateCheckInterval;
            await provider.CheckForUpdatesAsync(isManualCheck: false);

            Assert.Equal("v3.6.1", results[^1].LatestVersion);
        }

        [Fact]
        public async Task Automatic_checks_are_spaced_out()
        {
            var api = new FakeRuneberryApiClient { LatestRelease = new() { Version = "v3.6.0", Url = "https://x" }, LatestTag = "v3.6.1" };
            var (provider, results, _) = NewAutomatic(api);

            await provider.CheckForUpdatesAsync(isManualCheck: false);
            await provider.CheckForUpdatesAsync(isManualCheck: false);

            Assert.Single(results);
            Assert.Equal(0, api.TagLooks);
        }

        [Fact]
        public async Task Without_a_quick_look_the_full_check_waits_for_its_hour()
        {
            var api = new FakeRuneberryApiClient { LatestRelease = new() { Version = "v3.6.0", Url = "https://x" } };
            var (provider, results, clock) = NewAutomatic(api);
            await provider.CheckForUpdatesAsync(isManualCheck: false);

            clock.Now += CoreConstants.UpdateCheckInterval;
            await provider.CheckForUpdatesAsync(isManualCheck: false);
            Assert.Single(results);

            clock.Now += CoreConstants.FullUpdateCheckInterval;
            await provider.CheckForUpdatesAsync(isManualCheck: false);
            Assert.Equal(2, results.Count);
        }

        [Fact]
        public async Task An_announced_release_is_checked_at_once()
        {
            var api = new FakeRuneberryApiClient { LatestRelease = new() { Version = "v3.6.0", Url = "https://x" }, LatestTag = "v3.6.0" };
            var (provider, results, _) = NewAutomatic(api);
            await provider.CheckForUpdatesAsync(isManualCheck: false);

            api.LatestRelease = new() { Version = "v3.6.1", Url = "https://x" };
            await provider.CheckForUpdatesAsync(isManualCheck: false, releasePublished: true);

            Assert.Equal(2, results.Count);
            Assert.Equal("v3.6.1", results[^1].LatestVersion);
            Assert.False(results[^1].IsManualCheck);
        }

        [Fact]
        public async Task An_announced_release_respects_turned_off_update_checks()
        {
            var api = new FakeRuneberryApiClient { LatestRelease = new() { Version = "v3.6.1", Url = "https://x" } };
            var (provider, results, _) = NewAutomatic(api, checkForUpdates: false);

            await provider.CheckForUpdatesAsync(isManualCheck: false, releasePublished: true);

            Assert.Empty(results);
        }

        [Theory]
        [InlineData("https://github.com/o/r/releases/tag/v3.6.1", "v3.6.1")]
        [InlineData("https://github.com/o/r/releases/tag/v3.6.1-beta", "v3.6.1-beta")]
        [InlineData("https://github.com/o/r/releases", null)]
        [InlineData("https://github.com/o/r/releases/latest", null)]
        public void Reads_the_tag_from_the_latest_release_page_address(string url, string? expected)
        {
            Assert.Equal(expected, RuneberryApiClient.ParseTagFromReleaseUrl(new Uri(url)));
        }

        private sealed class StubUserPreferencesProvider : IUserPreferencesProvider
        {
            public bool CheckForUpdates { get; init; } = true;
            public event EventHandler<UserPreferences>? PreferencesSaved { add { } remove { } }
            public UserPreferences LoadPreferences() => new() { CheckForUpdates = CheckForUpdates };
            public void SavePreferences(UserPreferences preferences) { }
        }
    }
}
