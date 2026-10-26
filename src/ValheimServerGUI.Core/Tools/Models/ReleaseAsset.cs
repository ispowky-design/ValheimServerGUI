using Newtonsoft.Json;

namespace ValheimServerGUI.Tools.Models
{
    /// <summary>One downloadable file of a GitHub release (the installer, the portable exe, the checksums…).</summary>
    public class ReleaseAsset
    {
        [JsonProperty("name")]
        public string? Name { get; set; }

        [JsonProperty("browser_download_url")]
        public string? DownloadUrl { get; set; }

        [JsonProperty("size")]
        public long Size { get; set; }
    }
}
