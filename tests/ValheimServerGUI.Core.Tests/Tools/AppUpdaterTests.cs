using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Models;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Tools
{
    // The updater picks the release file matching how this copy was installed, downloads it, checks it against the
    // release's SHA256SUMS.txt, then installs it: the installer runs silently, the portable exe is swapped in place.
    public sealed class AppUpdaterTests : IDisposable
    {
        private const string Setup = "ValheimServerGUI-3.4.0-win-x64-setup.exe";
        private const string Portable = "ValheimServerGUI-3.4.0-win-x64-portable.exe";
        private static readonly byte[] SetupBytes = Encoding.UTF8.GetBytes("new installer");
        private static readonly byte[] PortableBytes = Encoding.UTF8.GetBytes("new portable exe");

        private readonly string _root = Path.Combine(Path.GetTempPath(), "vsg-updater-" + Guid.NewGuid().ToString("N"));
        private readonly string _appFolder;
        private readonly string _downloads;
        private readonly StubHttpClientProvider _http = new();
        private readonly List<ProcessStartInfo> _started = new();
        private string _sums = $"{Hash(SetupBytes)}  {Setup}\n{Hash(PortableBytes)}  {Portable}\n";

        public AppUpdaterTests()
        {
            _appFolder = Path.Combine(_root, "app");
            _downloads = Path.Combine(_root, "downloads");
            Directory.CreateDirectory(_appFolder);
            File.WriteAllText(ExePath, "old exe");
            _http.Respond = request => request.RequestUri!.AbsolutePath switch
            {
                var p when p.EndsWith(Setup) => Bytes(SetupBytes),
                var p when p.EndsWith(Portable) => Bytes(PortableBytes),
                var p when p.EndsWith("SHA256SUMS.txt") => Bytes(Encoding.UTF8.GetBytes(_sums)),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        private string ExePath => Path.Combine(_appFolder, "ValheimServerGUI.exe");

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

        private static ReleaseAsset Asset(string name, string? url = null)
            => new() { Name = name, DownloadUrl = url ?? $"https://github.com/o/r/releases/download/v3.4.0/{name}" };

        private static SoftwareUpdateEventArgs Update(params ReleaseAsset[] assets)
            => new("v3.4.0", "https://github.com/o/r/releases/tag/v3.4.0", isManualCheck: false, assets);

        private static SoftwareUpdateEventArgs FullRelease()
            => Update(Asset(Setup), Asset(Portable), Asset("ValheimServerGUI-3.4.0-linux-x64.tar.gz"), Asset("SHA256SUMS.txt"));

        private AppUpdater NewUpdater(bool installed = false, bool onWindows = true, bool isSingleFile = true)
        {
            if (installed) File.WriteAllText(Path.Combine(_appFolder, "unins000.exe"), "uninstaller");
            return new AppUpdater(_http, new FakeApplicationLogger(), ExePath, onWindows, isSingleFile, _downloads, _started.Add);
        }

        // ---- which file ----

        [Fact]
        public void An_installed_copy_updates_from_the_installer()
        {
            var updater = NewUpdater(installed: true);

            Assert.Equal(AppInstallKind.Installed, updater.InstallKind);
            Assert.Equal(Setup, updater.FindInstallableAsset(FullRelease())!.Name);
        }

        [Fact]
        public void A_portable_copy_updates_from_the_portable_exe()
        {
            var updater = NewUpdater();

            Assert.Equal(AppInstallKind.Portable, updater.InstallKind);
            Assert.Equal(Portable, updater.FindInstallableAsset(FullRelease())!.Name);
        }

        [Theory]
        [InlineData(false, true)]  // Linux
        [InlineData(true, false)]  // a development build of loose dlls
        public void Linux_and_development_builds_do_not_update_themselves(bool onWindows, bool isSingleFile)
        {
            var updater = NewUpdater(onWindows: onWindows, isSingleFile: isSingleFile);

            Assert.Equal(AppInstallKind.Unsupported, updater.InstallKind);
            Assert.Null(updater.FindInstallableAsset(FullRelease()));
        }

        [Fact]
        public void A_release_without_a_matching_file_offers_nothing_to_install()
        {
            Assert.Null(NewUpdater().FindInstallableAsset(Update(Asset(Setup))));
            Assert.Null(NewUpdater().FindInstallableAsset(new SoftwareUpdateEventArgs("v3.4.0", "https://x", false)));
        }

        [Theory]
        [InlineData("http://github.com/o/r/releases/download/v3.4.0/" + Portable)]
        [InlineData("not a url")]
        public void A_file_not_served_over_https_is_never_installed(string url)
        {
            Assert.Null(NewUpdater().FindInstallableAsset(Update(Asset(Portable, url))));
        }

        [Fact]
        public void A_file_name_with_a_folder_is_never_installed()
        {
            Assert.Null(NewUpdater().FindInstallableAsset(Update(Asset("..\\" + Portable))));
        }

        // ---- download ----

        [Fact]
        public async Task Downloads_the_file_and_checks_it_against_the_release_checksums()
        {
            var updater = NewUpdater();
            var progress = new List<double>();
            updater.DownloadProgressChanged += (_, p) => progress.Add(p);

            var file = await updater.DownloadAsync(FullRelease());

            Assert.Equal(Path.Combine(_downloads, Portable), file);
            Assert.Equal(PortableBytes, File.ReadAllBytes(file));
            Assert.Equal("v3.4.0", updater.DownloadedVersion);
            Assert.Equal(1.0, progress.Last());
            Assert.Contains(_http.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt"));
        }

        [Fact]
        public async Task A_file_whose_checksum_does_not_match_is_refused_and_deleted()
        {
            _sums = $"{Hash(Encoding.UTF8.GetBytes("something else"))}  {Portable}\n";
            var updater = NewUpdater();

            var e = await Assert.ThrowsAsync<Exception>(() => updater.DownloadAsync(FullRelease()));

            Assert.Equal(Strings.Update_ChecksumMismatch, e.Message);
            Assert.Null(updater.DownloadedVersion);
            Assert.Empty(Directory.GetFiles(_downloads));
        }

        [Fact]
        public async Task A_release_without_checksums_is_downloaded_unchecked()
        {
            var file = await NewUpdater().DownloadAsync(Update(Asset(Portable)));

            Assert.Equal(PortableBytes, File.ReadAllBytes(file));
        }

        [Fact]
        public async Task Asking_again_for_the_same_version_reuses_the_download()
        {
            var updater = NewUpdater();

            var first = updater.DownloadAsync(FullRelease());
            var second = updater.DownloadAsync(FullRelease());
            await first;

            Assert.Same(first, second);
            Assert.Single(_http.Requests, r => r.Request.RequestUri!.AbsolutePath.EndsWith(Portable));
        }

        [Fact]
        public async Task A_failed_download_is_tried_again()
        {
            var updater = NewUpdater();
            var respond = _http.Respond;
            _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
            await Assert.ThrowsAnyAsync<Exception>(() => updater.DownloadAsync(FullRelease()));

            _http.Respond = respond;
            var file = await updater.DownloadAsync(FullRelease());

            Assert.Equal(PortableBytes, File.ReadAllBytes(file));
        }

        [Theory]
        [InlineData("abc  file.exe\n", "abc")]
        [InlineData("abc *file.exe\r\n", "abc")]
        [InlineData("abc  other.exe\ndef  file.exe", "def")]
        [InlineData("abc  other.exe\n", null)]
        public void Reads_a_files_hash_from_sha256sum_output(string sums, string? expected)
        {
            Assert.Equal(expected, AppUpdater.ParseChecksum(sums, "file.exe"));
        }

        // ---- install ----

        [Fact]
        public async Task An_installed_copy_runs_the_installer_silently_and_asks_it_to_restart_the_app()
        {
            var updater = NewUpdater(installed: true);
            var file = await updater.DownloadAsync(FullRelease());

            updater.LaunchInstall(file);

            var start = Assert.Single(_started);
            Assert.Equal(file, start.FileName);
            Assert.Contains("/SILENT", start.Arguments);
            Assert.Contains("/relaunch=1", start.Arguments);
            Assert.Equal("old exe", File.ReadAllText(ExePath));
        }

        [Fact]
        public async Task A_portable_copy_swaps_its_exe_and_restarts_it()
        {
            var updater = NewUpdater();
            var file = await updater.DownloadAsync(FullRelease());

            updater.LaunchInstall(file);

            Assert.Equal(PortableBytes, File.ReadAllBytes(ExePath));
            Assert.Equal("old exe", File.ReadAllText(ExePath + ".old"));
            var start = Assert.Single(_started);
            Assert.Equal(ExePath, start.FileName);
            Assert.Equal($"{AppUpdater.AfterUpdateArg} {Environment.ProcessId}", start.Arguments);
        }

        [Fact]
        public void A_failed_portable_swap_puts_the_old_exe_back()
        {
            var updater = NewUpdater();

            Assert.ThrowsAny<IOException>(() => updater.LaunchInstall(Path.Combine(_downloads, "missing.exe")));

            Assert.Equal("old exe", File.ReadAllText(ExePath));
            Assert.False(File.Exists(ExePath + ".old"));
            Assert.Empty(_started);
        }

        [Fact]
        public async Task The_next_start_deletes_the_old_exe_and_the_downloads()
        {
            var updater = NewUpdater();
            updater.LaunchInstall(await updater.DownloadAsync(FullRelease()));

            NewUpdater().CleanUpPreviousUpdate();

            Assert.False(File.Exists(ExePath + ".old"));
            Assert.False(Directory.Exists(_downloads));
            Assert.True(File.Exists(ExePath));
        }

        [Fact]
        public void An_unsupported_copy_refuses_to_install()
        {
            Assert.Throws<InvalidOperationException>(() => NewUpdater(onWindows: false).LaunchInstall("x"));
        }

        // ---- restart arguments ----

        [Fact]
        public void The_restarted_exe_reads_the_old_process_id_and_keeps_the_other_arguments()
        {
            var args = new[] { "MyProfile", AppUpdater.AfterUpdateArg, "4242" };

            Assert.Equal(4242, AppUpdater.TakeAfterUpdateArgs(ref args));
            Assert.Equal(new[] { "MyProfile" }, args);
        }

        [Fact]
        public void A_normal_launch_has_no_process_to_wait_for()
        {
            var args = new[] { "MyProfile" };

            Assert.Null(AppUpdater.TakeAfterUpdateArgs(ref args));
            Assert.Equal(new[] { "MyProfile" }, args);
        }
    }
}
