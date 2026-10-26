using System.Collections.Generic;
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

        [JsonProperty("assets")]
        public List<ReleaseAsset>? Assets { get; set; }

        public LatestReleaseResponse ToLatestRelease() => new() { Version = TagName, Url = HtmlUrl, Assets = Assets };
    }
}
