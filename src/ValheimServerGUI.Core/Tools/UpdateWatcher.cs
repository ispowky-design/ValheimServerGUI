using System;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Tools
{
    /// <summary>
    /// Keeps checking for app updates while the app runs, not only at startup. It asks every
    /// <see cref="TickInterval"/>; <see cref="ISoftwareUpdateProvider"/> skips the checks that come sooner than
    /// <see cref="CoreConstants.UpdateCheckInterval"/> after the last one, or when the user turned checks off.
    /// </summary>
    public sealed class UpdateWatcher : IDisposable
    {
        internal static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(10);

        private readonly ISoftwareUpdateProvider _updates;
        private readonly IApplicationLogger _logger;
        private Timer? _timer;

        public UpdateWatcher(ISoftwareUpdateProvider updates, IApplicationLogger logger)
        {
            _updates = updates;
            _logger = logger;
        }

        public void Start() => _timer ??= new Timer(_ => _ = TickAsync(), null, TickInterval, TickInterval);

        internal async Task TickAsync()
        {
            try
            {
                await _updates.CheckForUpdatesAsync(isManualCheck: false);
            }
            catch (Exception e)
            {
                _logger.Warning("Periodic update check failed: {message}", e.Message);
            }
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
