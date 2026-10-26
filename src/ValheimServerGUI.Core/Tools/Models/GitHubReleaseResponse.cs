using Newtonsoft.Json;

namespace ValheimServerGUI.Tools.Models
{
    /// <summary>The fields the update check reads from GitHub's <c>releases/latest</c> answer.</summary>
    public class GitHubReleaseResponse
    {
        [JsonProperty("tag_name")]
        public string? TagName { get; set; }

        [JsonProperty("html_url")]
        public string? HtmlUrl { get; set; }

        public LatestReleaseResponse ToLatestRelease() => new() { Version = TagName, Url = HtmlUrl };
    }
}
