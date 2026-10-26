using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ValheimServerGUI.Game;
using ValheimServerGUI.Tools.Models;

namespace ValheimServerGUI.Tools
{
    public interface ISoftwareUpdateProvider
    {
        event EventHandler UpdateCheckStarted;

        event EventHandler<SoftwareUpdateEventArgs> UpdateCheckFinished;

        /// <summary>The result of the most recent completed check, or null if none has finished yet. Lets a
        /// window created after the startup check (which runs before the main window exists) show its result.</summary>
        SoftwareUpdateEventArgs? LastResult { get; }

        /// <param name="isManualCheck">The user asked (Help &gt; Check for Updates): always checks, and reports the result.</param>
        /// <param name="releasePublished">A release was just announced (see <see cref="UpdateWatcher"/>): an automatic check
        /// that skips the spacing and the quick tag look, so the new release is found at once.</param>
        Task CheckForUpdatesAsync(bool isManualCheck, bool releasePublished = false);
    }

    public class SoftwareUpdateEventArgs
    {
        public string? LatestVersion { get; }

        /// <summary>The latest release's page, or null when there is none to link to (no release qualifies, or
        /// the check failed).</summary>
        public string? ReleaseUrl { get; }

        /// <summary>The latest release's files; empty when the answer did not list them or the check failed.</summary>
        public IReadOnlyList<ReleaseAsset> Assets { get; } = Array.Empty<ReleaseAsset>();

        public bool IsManualCheck { get; }

        public bool IsSuccessful { get; }

        public Exception? Exception { get; }

        public SoftwareUpdateEventArgs(
            string latestVersion,
            string? releaseUrl,
            bool isManualCheck,
            IReadOnlyList<ReleaseAsset>? assets = null)
        {
            LatestVersion = latestVersion;
            ReleaseUrl = releaseUrl;
            Assets = assets ?? Array.Empty<ReleaseAsset>();
            IsManualCheck = isManualCheck;
            IsSuccessful = true;
        }

        public SoftwareUpdateEventArgs(
            Exception e,
            bool isManualCheck)
        {
            Exception = e;
            IsManualCheck = isManualCheck;
            IsSuccessful = false;
        }
    }

    public class SoftwareUpdateProvider : ISoftwareUpdateProvider
    {
        private readonly IRuneberryApiClient ApiClient;
        private readonly IUserPreferencesProvider UserPrefsProvider;

        private readonly TimeSpan UpdateCheckInterval = CoreConstants.UpdateCheckInterval;
        private DateTime NextAutomaticUpdateCheck = DateTime.MinValue;
        private DateTime NextFullUpdateCheck = DateTime.MinValue;

        /// <summary>Test seam for the clock.</summary>
        internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public SoftwareUpdateProvider(IRuneberryApiClient apiClient, IUserPreferencesProvider userPrefsProvider)
        {
            ApiClient = apiClient;
            UserPrefsProvider = userPrefsProvider;
        }

        public event EventHandler? UpdateCheckStarted;

        public event EventHandler<SoftwareUpdateEventArgs>? UpdateCheckFinished;

        public SoftwareUpdateEventArgs? LastResult { get; private set; }

        public async Task CheckForUpdatesAsync(bool isManualCheck, bool releasePublished = false)
        {
            if (!isManualCheck && releasePublished)
            {
                if (!UserPrefsProvider.LoadPreferences().CheckForUpdates) return;
            }
            else if (!isManualCheck)
            {
                // Only fulfill automated checks if enough time has passed since the last check
                var now = UtcNow();
                if (now < NextAutomaticUpdateCheck) return;
                NextAutomaticUpdateCheck = now + UpdateCheckInterval;

                // Only fulfill automated checks if the user has update checks enabled
                var prefs = UserPrefsProvider.LoadPreferences();
                if (!prefs.CheckForUpdates) return;

                // Once a full check has answered, later automatic checks first look at the latest tag, which is cheap,
                // and only run the full check (and tell anyone) when a new release appeared.
                if (LastResult is { IsSuccessful: true } && await IsLatestTagUnchangedAsync(now)) return;
            }

            UpdateCheckStarted?.Invoke(this, EventArgs.Empty);

            SoftwareUpdateEventArgs eventArgs;

            try
            {
                var release = await ApiClient.GetLatestReleaseAsync();

                // No qualifying release: treat the running version as the latest (nothing newer to offer).
                eventArgs = release == null
                    ? new SoftwareUpdateEventArgs(AssemblyHelper.GetApplicationVersion(), null, isManualCheck)
                    : new SoftwareUpdateEventArgs(release.Version!, release.Url, isManualCheck, release.Assets);
            }
            catch (Exception e)
            {
                eventArgs = new SoftwareUpdateEventArgs(e, isManualCheck);
            }

            LastResult = eventArgs;
            NextFullUpdateCheck = UtcNow() + CoreConstants.FullUpdateCheckInterval;
            UpdateCheckFinished?.Invoke(this, eventArgs);
        }

        // True when nothing new needs checking: the latest tag is still the one the last full check found, or (without
        // a quick look) the last full check is recent. A failed quick look also waits for the next tick.
        private async Task<bool> IsLatestTagUnchangedAsync(DateTime now)
        {
            string? tag;
            try
            {
                tag = await ApiClient.GetLatestTagAsync();
            }
            catch
            {
                return true;
            }

            if (tag == null) return now < NextFullUpdateCheck;
            return string.Equals(tag, LastResult!.LatestVersion, StringComparison.OrdinalIgnoreCase);
        }
    }
}
