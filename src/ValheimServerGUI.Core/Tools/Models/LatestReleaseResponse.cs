using System.Collections.Generic;
using Newtonsoft.Json;

namespace ValheimServerGUI.Tools.Models
{
    /// <summary>The API's <c>/update-check</c> answer: the latest release's tag name and its release page.</summary>
    public class LatestReleaseResponse
    {
        [JsonProperty("version")]
        public string? Version { get; set; }

        [JsonProperty("url")]
        public string? Url { get; set; }

        /// <summary>The release's files, when the answer lists them (GitHub's does; the API's does not). The app
        /// downloads and installs an update itself only from these.</summary>
        [JsonProperty("assets")]
        public List<ReleaseAsset>? Assets { get; set; }
    }
}
