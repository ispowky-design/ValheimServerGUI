using System;
using System.IO;
using System.Threading;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Game
{
    /// <summary>
    /// Starts every crashed server again once its <see cref="ValheimServer.CrashRestartDueAt"/> has come, app-wide.
    /// The world is backed up first (as a "Scheduled" backup, so only the newest few are kept), keeping the last
    /// save as it was in case the next run damages it.
    /// </summary>
    public sealed class CrashRestartService : IDisposable
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(2);

        private readonly IServerManager _servers;
        private readonly IWorldBackupService _backups;
        private readonly IApplicationLogger _logger;
        private Timer? _timer;

        public CrashRestartService(IServerManager servers, IWorldBackupService backups, IApplicationLogger logger)
        {
            _servers = servers;
            _backups = backups;
            _logger = logger;
        }

        /// <summary>Starts watching for due restarts (idempotent).</summary>
        public void Start()
        {
            _timer ??= new Timer(_ => CheckDue(DateTime.Now), null, CheckInterval, CheckInterval);
        }

        /// <summary>Starts each stopped server whose crash restart is due at or before <paramref name="now"/>.</summary>
        public void CheckDue(DateTime now)
        {
            foreach (var server in _servers.All)
            {
                try
                {
                    if (server.Status != ServerStatus.Stopped || server.CrashRestartDueAt is not { } due || due > now) continue;

                    server.RestartAfterCrash(BackUpWorld);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Crash restart failed: {message}", ex.Message);
                }
            }
        }

        private void BackUpWorld(IValheimServerOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.SaveDataFolderPath) || string.IsNullOrWhiteSpace(options.WorldName)) return;

            var saveFolder = new DirectoryInfo(options.SaveDataFolderPath);
            if (!_backups.WorldExists(saveFolder, options.WorldName)) return;

            var backup = _backups.CreateBackup(saveFolder, options.WorldName, WorldBackupKind.Scheduled);
            var pruned = _backups.PruneScheduledBackups(saveFolder, options.WorldName, CoreConstants.ScheduledBackupsKept);
            _logger.Information("Crash restart: backed up world {world} to {path} ({pruned} old scheduled backups deleted)",
                options.WorldName, backup.Path, pruned);
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
