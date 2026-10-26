using System;
using System.IO;
using System.Threading;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Game
{
    /// <summary>
    /// Restarts every running server whose <see cref="ValheimServer.NextScheduledRestart"/> has come, app-wide (a
    /// server keeps its schedule whichever window, if any, shows it). When its profile asks for it, the world is
    /// backed up while the server is stopped, then the oldest scheduled backups beyond
    /// <see cref="CoreConstants.ScheduledBackupsKept"/> are deleted.
    /// </summary>
    public sealed class ScheduledRestartService : IDisposable
    {
        private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(15);

        private readonly IServerManager _servers;
        private readonly IWorldBackupService _backups;
        private readonly IApplicationLogger _logger;
        private Timer? _timer;

        public ScheduledRestartService(IServerManager servers, IWorldBackupService backups, IApplicationLogger logger)
        {
            _servers = servers;
            _backups = backups;
            _logger = logger;
        }

        /// <summary>Starts checking the schedules (idempotent).</summary>
        public void Start()
        {
            _timer ??= new Timer(_ => CheckDue(DateTime.Now), null, CheckInterval, CheckInterval);
        }

        /// <summary>Restarts each running server whose scheduled time is at or before <paramref name="now"/>.</summary>
        public void CheckDue(DateTime now)
        {
            foreach (var server in _servers.All)
            {
                try
                {
                    if (server.Status != ServerStatus.Running || server.NextScheduledRestart is not { } due || due > now) continue;

                    _logger.Information("Scheduled restart due at {due:t} for server {name}", due, server.Options.Name);
                    server.ScheduledRestart(server.Options.BackupOnScheduledRestart ? BackUpWorld : null);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Scheduled restart check failed: {message}", ex.Message);
                }
            }
        }

        // Runs while the server is stopped, so the save files are complete and closed.
        private void BackUpWorld(IValheimServerOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.SaveDataFolderPath) || string.IsNullOrWhiteSpace(options.WorldName)) return;

            var saveFolder = new DirectoryInfo(options.SaveDataFolderPath);
            if (!_backups.WorldExists(saveFolder, options.WorldName))
            {
                _logger.Warning("Scheduled restart: world {world} has no save files to back up yet", options.WorldName);
                return;
            }

            var backup = _backups.CreateBackup(saveFolder, options.WorldName, WorldBackupKind.Scheduled);
            var pruned = _backups.PruneScheduledBackups(saveFolder, options.WorldName, CoreConstants.ScheduledBackupsKept);
            _logger.Information("Scheduled restart: backed up world {world} to {path} ({pruned} old scheduled backups deleted)",
                options.WorldName, backup.Path, pruned);
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}
