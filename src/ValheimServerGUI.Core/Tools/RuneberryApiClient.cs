using Newtonsoft.Json;
using Semver;
using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Http;
using ValheimServerGUI.Tools.Models;

namespace ValheimServerGUI.Tools
{
    public interface IRuneberryApiClient
    {
        Task SendCrashReportAsync(CrashReport report);

        /// <summary>
        /// The latest release eligible for update notices (the API picks it: newest published, non-draft,
        /// non-pre-release release with an asset). Null when no release qualifies. Throws when the API can't be
        /// reached, answers with an error, or returns a response without a parseable version and a URL.
        /// </summary>
        Task<LatestReleaseResponse?> GetLatestReleaseAsync();

        /// <summary>
        /// The newest release's tag, read cheaply from the latest-release page's redirect (no API rate limit), or null
        /// when this build has no such quick look (builds with an API key) or the page answered no tag. Throws when the
        /// page can't be reached.
        /// </summary>
        Task<string?> GetLatestTagAsync();
    }

    public class RuneberryApiClient : RestClient, IRuneberryApiClient
    {
        private readonly RuneberryApiKey _apiKey;

        public RuneberryApiClient(IRestClientContext context, RuneberryApiKey apiKey) : base(context)
        {
            _apiKey = apiKey;
        }

        #region IRuneberryApiClient implementation

        public async Task SendCrashReportAsync(CrashReport report)
        {
            var response = await Post($"{CoreConstants.UrlRuneberryApi}/crash-report", report)
                .WithRuneberryApiKey(_apiKey)
                .SendAsync();

            if (response == null || !response.IsSuccessStatusCode)
            {
                string message;

                try
                {
                    if (response != null)
                    {
                        var rawResponse = await response.Content.ReadAsStringAsync();
                        var exceptionResponse = JsonConvert.DeserializeObject<ErrorResponse>(rawResponse);
                        message = $"({(int)response.StatusCode}) {exceptionResponse?.Message}";
                    }
                    else
                    {
                        message = Strings.Api_RuneberryUnreachable;
                    }
                }
                catch
                {
                    message = Strings.Api_UnknownError;
                }

                throw new Exception(message);
            }
        }

        public async Task<LatestReleaseResponse?> GetLatestReleaseAsync()
        {
            // Without a key the Runeberry API refuses the check, so ask this fork's GitHub releases instead.
            var url = _apiKey.IsEmpty ? CoreConstants.UrlGitHubLatestRelease : $"{CoreConstants.UrlRuneberryApi}/update-check";
            var request = _apiKey.IsEmpty
                ? Get(url).WithHeader("User-Agent", "ValheimServerGUI").WithHeader("Accept", "application/vnd.github+json")
                : Get(url).WithRuneberryApiKey(_apiKey);
            var response = await request.SendAsync();

            if (response?.StatusCode == HttpStatusCode.NotFound) return null;
            if (response == null || !response.IsSuccessStatusCode) throw new Exception(Strings.Api_UpdateServerUnreachable);

            LatestReleaseResponse? release;
            try
            {
                var json = await response.Content.ReadAsStringAsync();
                release = _apiKey.IsEmpty
                    ? JsonConvert.DeserializeObject<GitHubReleaseResponse>(json)?.ToLatestRelease()
                    : JsonConvert.DeserializeObject<LatestReleaseResponse>(json);
            }
            catch (JsonException)
            {
                release = null;
            }

            // An unusable answer fails the check the same way an error status does.
            if (release == null
                || string.IsNullOrWhiteSpace(release.Url)
                || !SemVersion.TryParse(release.Version, SemVersionStyles.Any, out _))
            {
                Logger.Error("Update check returned an unusable response: {0}", url);
                throw new Exception(Strings.Api_UpdateServerUnreachable);
            }

            return release;
        }

        public async Task<string?> GetLatestTagAsync()
        {
            if (!_apiKey.IsEmpty) return null;

            using var client = Context.HttpClientProvider.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Head, CoreConstants.UrlGitHubLatestReleasePage);
            request.Headers.UserAgent.ParseAdd("ValheimServerGUI");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            // The page redirects to …/releases/tag/<tag>; the client follows it, so the final address carries the tag.
            return ParseTagFromReleaseUrl(response.RequestMessage?.RequestUri);
        }

        internal static string? ParseTagFromReleaseUrl(Uri? url)
        {
            const string marker = "/releases/tag/";
            var path = url?.AbsolutePath;
            var index = path?.IndexOf(marker, StringComparison.Ordinal) ?? -1;
            if (index < 0) return null;
            var tag = Uri.UnescapeDataString(path![(index + marker.Length)..].TrimEnd('/'));
            return tag.Length == 0 ? null : tag;
        }

        #endregion
    }

    internal static class RuneberryApiRequestExtensions
    {
        /// <summary>Attaches the client API key every Runeberry API route requires.</summary>
        public static RestClientRequest WithRuneberryApiKey(this RestClientRequest request, RuneberryApiKey apiKey)
            => request.WithHeader(apiKey.Header, apiKey.Value);
    }
}
