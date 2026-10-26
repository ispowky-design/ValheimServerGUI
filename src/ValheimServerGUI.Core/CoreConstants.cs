using System;

namespace ValheimServerGUI
{
    /// <summary>
    /// OS-independent constants consumed by the service layer. Paths differ per OS, so they are not here:
    /// they are resolved through <c>IValheimPathResolver</c> instead.
    /// </summary>
    public static class CoreConstants
    {
        /// <summary>Default Valheim dedicated-server port.</summary>
        public const int DefaultServerPort = 2456;

        /// <summary>Default auto-save interval, in seconds.</summary>
        public const int DefaultSaveInterval = 1800;

        /// <summary>Default number of world backups to retain.</summary>
        public const int DefaultBackupCount = 4;

        /// <summary>Default short backup interval, in seconds.</summary>
        public const int DefaultBackupIntervalShort = 7200;

        /// <summary>Default long backup interval, in seconds.</summary>
        public const int DefaultBackupIntervalLong = 43200;

        /// <summary>
        /// Default wait, in seconds, before a stop that has not finished offers Force Stop. Well above the ~34 s a
        /// stop is deferred by world generation on Windows, so a normal slow stop never shows the offer.
        /// </summary>
        public const int DefaultForceStopOfferSeconds = 120;

        /// <summary>The TCP port the World Map's web page is served on unless the user picks another.</summary>
        public const int DefaultMapWebPort = 8080;

        /// <summary>How many scheduled-restart backups each world keeps; older ones are deleted.</summary>
        public const int ScheduledBackupsKept = 10;

        /// <summary>Name of the default server profile.</summary>
        public const string DefaultServerProfileName = "Default";

        /// <summary>
        /// Valheim's Steam App ID. Passed as the <c>SteamAppId</c> environment variable when launching
        /// the server and used as a path segment when locating Steam Cloud saves.
        /// </summary>
        public const string ValheimSteamAppId = "892970";

        /// <summary>How often the open app looks for a new release: a quick look at the latest tag, followed by a full
        /// check only when the tag changed (see <see cref="Tools.IRuneberryApiClient.GetLatestTagAsync"/>).</summary>
        public static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromSeconds(30);

        /// <summary>How often to run a full update check when no quick tag look is available (builds with an API key).</summary>
        public static readonly TimeSpan FullUpdateCheckInterval = TimeSpan.FromHours(1);

        /// <summary>
        /// The app's backend base (crash reports, update check, external-IP check), served by the
        /// Cloudflare Worker in the <c>ValheimServerGUI.Api</c> repo. The Worker checks requests against
        /// <c>ClientSecrets.RuneberryClientApiKey</c>. The client appends the route (<c>/crash-report</c>,
        /// <c>/update-check</c>, <c>/ip-check</c>).
        /// </summary>
        public const string UrlRuneberryApi = "https://api.runeberry.com/vsg";

        /// <summary>
        /// This fork's newest published, non-pre-release GitHub release (the GitHub REST API answers 404 when
        /// there is none). Builds without a Runeberry API key (see <see cref="Tools.RuneberryApiKey.IsEmpty"/>)
        /// check for updates here instead of <see cref="UrlRuneberryApi"/>, which refuses them.
        /// </summary>
        public const string UrlGitHubLatestRelease = "https://api.github.com/repos/ispowky-design/ValheimServerGUI/releases/latest";

        /// <summary>
        /// The fork's latest-release web page. It redirects to <c>…/releases/tag/&lt;tag&gt;</c>, so a request to it tells the
        /// newest tag without using the GitHub API and its 60-requests-an-hour limit.
        /// </summary>
        /// <summary>
        /// The ntfy.sh topic the release workflow posts to as soon as it publishes a release (.github/workflows/build.yml).
        /// The open app listens to it and checks GitHub at once. Anyone can post to a public topic, so a message is only
        /// a hint to check: what gets installed always comes from the GitHub release and its checksums.
        /// </summary>
        public const string UrlReleasePushTopic = "https://ntfy.sh/valheimservergui-ispowky-releases";

        public const string UrlGitHubLatestReleasePage = "https://github.com/ispowky-design/ValheimServerGUI/releases/latest";

        /// <summary>
        /// Public IPv4-only lookup (<c>{"ip":…}</c>) used for the external IP by builds without a Runeberry API key.
        /// </summary>
        public const string UrlPublicIpCheck = "https://api.ipify.org/?format=json";
    }
}
