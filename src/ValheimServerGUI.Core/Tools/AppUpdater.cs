using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Http;
using ValheimServerGUI.Tools.Logging;
using ValheimServerGUI.Tools.Models;

namespace ValheimServerGUI.Tools
{
    /// <summary>How this copy of the app was installed, which decides how it updates itself.</summary>
    public enum AppInstallKind
    {
        /// <summary>Not a Windows single-file build (Linux, or a development build): updates go through the release page.</summary>
        Unsupported,

        /// <summary>Installed by the Windows installer: an update runs the new installer silently.</summary>
        Installed,

        /// <summary>The portable Windows exe: an update swaps the exe in place.</summary>
        Portable,
    }

    /// <summary>
    /// Downloads a newer release from the update check's answer and installs it over this copy of the app.
    /// Installing ends with the app restarting, so the caller makes sure no server is running first (closing the
    /// app stops its servers) and exits right after <see cref="LaunchInstall"/>.
    /// </summary>
    public interface IAppUpdater
    {
        AppInstallKind InstallKind { get; }

        /// <summary>The release file this copy can install itself from, or null (no matching file, or an install
        /// kind that cannot update itself).</summary>
        ReleaseAsset? FindInstallableAsset(SoftwareUpdateEventArgs update);

        /// <summary>The version whose download has finished and been verified, or null.</summary>
        string? DownloadedVersion { get; }

        /// <summary>The running download's progress, from 0 to 1.</summary>
        event EventHandler<double>? DownloadProgressChanged;

        /// <summary>
        /// Downloads the update's installable file and checks it against the release's SHA256SUMS.txt. Returns
        /// the downloaded file. Asking again for the same version returns the same download, running or done.
        /// </summary>
        Task<string> DownloadAsync(SoftwareUpdateEventArgs update);

        /// <summary>Starts installing a file <see cref="DownloadAsync"/> returned; the app must exit right after.</summary>
        void LaunchInstall(string downloadedFile);

        /// <summary>Deletes what the previous update left behind (the replaced portable exe, the downloads).</summary>
        void CleanUpPreviousUpdate();

        /// <summary>True the first time it is asked for <paramref name="version"/>: the one window that tells the user
        /// an update is ready. Every later ask, from any window, is false.</summary>
        bool ClaimReadyNotice(string version);
    }

    public class AppUpdater : IAppUpdater
    {
        /// <summary>The argument a swapped portable exe is restarted with, followed by the old process's id: the new
        /// process waits for the old one to exit before taking the single-instance lock.</summary>
        public const string AfterUpdateArg = "--after-update";

        private const string InstallerSuffix = "-win-x64-setup.exe";
        private const string PortableSuffix = "-win-x64-portable.exe";
        private const string ChecksumsName = "SHA256SUMS.txt";
        private const string ReplacedExeSuffix = ".old";

        private readonly IHttpClientProvider _http;
        private readonly IApplicationLogger _logger;
        private readonly string _exePath;
        private readonly string _downloadFolder;
        private readonly Action<ProcessStartInfo> _startProcess;
        private readonly object _lock = new();
        private (string Version, Task<string> Task)? _download;
        private readonly HashSet<string> _noticed = new();

        public AppUpdater(IHttpClientProvider http, IApplicationLogger logger)
            : this(http, logger,
                exePath: Environment.ProcessPath ?? string.Empty,
                onWindows: OperatingSystem.IsWindows(),
                // A release is one bundled exe; a development build runs from loose dlls next to it.
                isSingleFile: !File.Exists(Path.Combine(AppContext.BaseDirectory, "ValheimServerGUI.Core.dll")),
                downloadFolder: Path.Combine(Path.GetTempPath(), "ValheimServerGUI-update"),
                startProcess: info => Process.Start(info)?.Dispose())
        {
        }

        internal AppUpdater(
            IHttpClientProvider http, IApplicationLogger logger, string exePath, bool onWindows, bool isSingleFile,
            string downloadFolder, Action<ProcessStartInfo> startProcess)
        {
            _http = http;
            _logger = logger;
            _exePath = exePath;
            _downloadFolder = downloadFolder;
            _startProcess = startProcess;
            InstallKind = DetectInstallKind(exePath, onWindows, isSingleFile);
        }

        public AppInstallKind InstallKind { get; }

        public string? DownloadedVersion { get; private set; }

        public event EventHandler<double>? DownloadProgressChanged;

        // The installer leaves its uninstaller next to the exe; the portable exe stands alone.
        private static AppInstallKind DetectInstallKind(string exePath, bool onWindows, bool isSingleFile)
        {
            if (!onWindows || !isSingleFile || string.IsNullOrEmpty(exePath)) return AppInstallKind.Unsupported;
            var folder = Path.GetDirectoryName(exePath);
            return folder != null && File.Exists(Path.Combine(folder, "unins000.exe"))
                ? AppInstallKind.Installed
                : AppInstallKind.Portable;
        }

        public ReleaseAsset? FindInstallableAsset(SoftwareUpdateEventArgs update)
        {
            var suffix = InstallKind switch
            {
                AppInstallKind.Installed => InstallerSuffix,
                AppInstallKind.Portable => PortableSuffix,
                _ => null,
            };
            if (suffix == null || !update.IsSuccessful) return null;

            return update.Assets.FirstOrDefault(a =>
                a.Name != null
                && a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && a.Name.IndexOfAny(new[] { '/', '\\' }) < 0
                && Uri.TryCreate(a.DownloadUrl, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps);
        }

        public Task<string> DownloadAsync(SoftwareUpdateEventArgs update)
        {
            var asset = FindInstallableAsset(update)
                ?? throw new InvalidOperationException("The update has no file this copy can install.");
            var version = update.LatestVersion!;

            lock (_lock)
            {
                // A running or finished download of this version is reused; a failed one is tried again.
                if (_download is { } d && d.Version == version && !d.Task.IsFaulted && !d.Task.IsCanceled)
                    return d.Task;

                var task = DownloadCoreAsync(version, asset, update.Assets);
                _download = (version, task);
                return task;
            }
        }

        private async Task<string> DownloadCoreAsync(string version, ReleaseAsset asset, IReadOnlyList<ReleaseAsset> assets)
        {
            await Task.Yield();
            Directory.CreateDirectory(_downloadFolder);
            var target = Path.Combine(_downloadFolder, asset.Name!);
            var partial = target + ".part";

            _logger.Information("Downloading update {version} from {url}", version, asset.DownloadUrl);
            using var client = _http.CreateClient();
            try
            {
                using (var response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? asset.Size;
                    await using var source = await response.Content.ReadAsStreamAsync();
                    await using var file = File.Create(partial);
                    var buffer = new byte[81920];
                    long received = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer)) > 0)
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read));
                        received += read;
                        if (total > 0) DownloadProgressChanged?.Invoke(this, Math.Min(1.0, (double)received / total));
                    }
                }

                await VerifyChecksumAsync(client, partial, asset.Name!, assets);
                File.Move(partial, target, overwrite: true);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }

            _logger.Information("Update {version} downloaded to {path}", version, target);
            DownloadedVersion = version;
            DownloadProgressChanged?.Invoke(this, 1.0);
            return target;
        }

        // The release publishes SHA256SUMS.txt ("<hash>  <file name>" per line). A release without it is installed
        // unchecked; one that lists the file with another hash is refused.
        private async Task VerifyChecksumAsync(HttpClient client, string file, string name, IReadOnlyList<ReleaseAsset> assets)
        {
            var sums = assets.FirstOrDefault(a => a.Name == ChecksumsName);
            if (sums?.DownloadUrl == null)
            {
                _logger.Warning("Update {name} has no {sums} to check it against", name, ChecksumsName);
                return;
            }

            var expected = ParseChecksum(await client.GetStringAsync(sums.DownloadUrl), name);
            if (expected == null)
            {
                _logger.Warning("{sums} does not list {name}", ChecksumsName, name);
                return;
            }

            string actual;
            await using (var stream = File.OpenRead(file))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Error("Update {name} checksum mismatch: expected {expected}, got {actual}", name, expected, actual);
                throw new Exception(Strings.Update_ChecksumMismatch);
            }
        }

        /// <summary>The hash <paramref name="sums"/> (sha256sum output) gives for <paramref name="name"/>, or null.</summary>
        internal static string? ParseChecksum(string sums, string name)
        {
            foreach (var line in sums.Split('\n'))
            {
                var parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].TrimStart('*') == name) return parts[0];
            }
            return null;
        }

        public void LaunchInstall(string downloadedFile)
        {
            switch (InstallKind)
            {
                case AppInstallKind.Installed:
                    // The installer closes this app if it is still up, installs over it, then starts the new version
                    // (relaunch=1, see packaging/windows/ValheimServerGUI.iss). Shell execute lets it ask for elevation
                    // when the previous install was for all users.
                    _logger.Information("Starting the update installer {path}", downloadedFile);
                    _startProcess(new ProcessStartInfo(downloadedFile)
                    {
                        Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /relaunch=1",
                        UseShellExecute = true,
                    });
                    break;

                case AppInstallKind.Portable:
                    // Windows lets a running exe be renamed, not overwritten: move it aside, put the new one in its
                    // place, and start it. It waits for this process to exit, then deletes the old exe.
                    var replaced = _exePath + ReplacedExeSuffix;
                    TryDelete(replaced);
                    File.Move(_exePath, replaced);
                    try
                    {
                        File.Move(downloadedFile, _exePath);
                    }
                    catch
                    {
                        File.Move(replaced, _exePath);
                        throw;
                    }

                    _logger.Information("Replaced {path}; restarting it", _exePath);
                    _startProcess(new ProcessStartInfo(_exePath)
                    {
                        Arguments = $"{AfterUpdateArg} {Environment.ProcessId}",
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(_exePath) ?? string.Empty,
                    });
                    break;

                default:
                    throw new InvalidOperationException("This copy of the app cannot install updates itself.");
            }
        }

        public bool ClaimReadyNotice(string version)
        {
            lock (_lock) return _noticed.Add(version);
        }

        public void CleanUpPreviousUpdate()
        {
            if (!string.IsNullOrEmpty(_exePath)) TryDelete(_exePath + ReplacedExeSuffix);
            try
            {
                if (Directory.Exists(_downloadFolder)) Directory.Delete(_downloadFolder, recursive: true);
            }
            catch (Exception e)
            {
                // The installer that just ran may still hold its file; the next start deletes it.
                _logger.Debug("Could not delete {folder}: {message}", _downloadFolder, e.Message);
            }
        }

        /// <summary>
        /// Removes <see cref="AfterUpdateArg"/> and its process id from <paramref name="args"/>, so the rest reads as
        /// a normal launch. Returns the id of the process to wait for, or null.
        /// </summary>
        public static int? TakeAfterUpdateArgs(ref string[] args)
        {
            var index = Array.IndexOf(args, AfterUpdateArg);
            if (index < 0) return null;

            int? pid = index + 1 < args.Length && int.TryParse(args[index + 1], out var id) ? id : null;
            var count = pid.HasValue ? 2 : 1;
            args = args.Take(index).Concat(args.Skip(index + count)).ToArray();
            return pid;
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e)
            {
                _logger.Debug("Could not delete {path}: {message}", path, e.Message);
            }
        }
    }
}
