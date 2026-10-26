using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game;

/// <summary>
/// SteamCMD install/update against a fake <c>steamcmd.sh</c> (a shell script that records its arguments and prints
/// what SteamCMD prints): the download-and-unpack of SteamCMD itself, the install command line, progress parsing,
/// success/failure judged from the output, and the build ids read from the app manifest and from app_info_print.
/// The script-driven tests run on Linux/macOS only.
/// </summary>
public sealed class SteamCmdServiceTests : IDisposable
{
    private readonly string _root;
    private readonly LinuxValheimPathResolver _paths;
    private readonly StubHttpClientProvider _http = new();
    private readonly SteamCmdService _service;

    public SteamCmdServiceTests()
    {
        _root = Path.Join(Path.GetTempPath(), "vsg_steamcmd_" + Guid.NewGuid().ToString("N"));
        _paths = new LinuxValheimPathResolver(Path.Join(_root, "home"), Path.Join(_root, "data"));
        _service = new SteamCmdService(_paths, _http, new FakeApplicationLogger(), onWindows: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string ArgsFile => Path.Join(_root, "args.txt");

    private string RunsFile => Path.Join(_root, "runs.txt");

    // A stand-in SteamCMD: appends its arguments (one per line) and prints the given output.
    private string ScriptBody(params string[] output)
        => "#!/bin/sh\nprintf '%s\\n' \"$@\" >> '" + ArgsFile + "'\n" + string.Concat(output.Select(l => "echo '" + l.Replace("'", "'\"'\"'") + "'\n"));

    // A stand-in SteamCMD that prints <paramref name="error"/> on its first <paramref name="failures"/> runs, then
    // succeeds; it counts its runs in RunsFile.
    private void InstallFlakySteamCmd(int failures, string error)
    {
        Directory.CreateDirectory(_service.SteamCmdFolder);
        var path = Path.Join(_service.SteamCmdFolder, "steamcmd.sh");
        File.WriteAllText(path,
            "#!/bin/sh\n"
            + "n=$(cat '" + RunsFile + "' 2>/dev/null || echo 0); n=$((n+1)); echo $n > '" + RunsFile + "'\n"
            + "if [ $n -le " + failures + " ]; then echo \"" + error + "\"; else echo \"Success! App '896660' fully installed.\"; fi\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private int Runs => int.Parse(File.ReadAllText(RunsFile).Trim());

    private void InstallFakeSteamCmd(params string[] output)
    {
        Directory.CreateDirectory(_service.SteamCmdFolder);
        var path = Path.Join(_service.SteamCmdFolder, "steamcmd.sh");
        File.WriteAllText(path, ScriptBody(output));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class ListProgress : IProgress<SteamCmdProgress>
    {
        public List<SteamCmdProgress> Reports { get; } = new();
        public void Report(SteamCmdProgress value) { lock (Reports) Reports.Add(value); }
    }

    [Theory]
    [InlineData(" Update state (0x61) downloading, progress: 45.12 (100 / 200)", 45.12)]
    [InlineData(" Update state (0x81) verifying update, progress: 100.00 (1 / 1)", 100.0)]
    [InlineData("Logging in user 'anonymous' to Steam Public...OK", null)]
    public void ParsePercent_ReadsTheProgressFigure(string line, double? expected)
    {
        Assert.Equal(expected, SteamCmdService.ParsePercent(line));
    }

    [Fact]
    public void ParseLatestBuildId_TakesThePublicBranch()
    {
        const string dump = """
            "896660"
            {
                "common" { "name" "Valheim Dedicated Server" "buildid" "1" }
                "depots"
                {
                    "branches"
                    {
                        "public_test" { "buildid" "20000002" "timeupdated" "1" }
                        "public" { "buildid" "19999001" "timeupdated" "1700000000" }
                    }
                }
            }
            """;

        Assert.Equal("19999001", SteamCmdService.ParseLatestBuildId(dump));
        Assert.Null(SteamCmdService.ParseLatestBuildId("no app info here"));
    }

    [Fact]
    public void GetInstalledBuildId_ReadsTheAppManifest()
    {
        var install = Path.Join(_root, "server");
        Assert.Null(_service.GetInstalledBuildId(install));

        Directory.CreateDirectory(Path.Join(install, "steamapps"));
        File.WriteAllText(Path.Join(install, "steamapps", "appmanifest_896660.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"896660\"\n\t\"buildid\"\t\t\"19999001\"\n}\n");

        Assert.Equal("19999001", _service.GetInstalledBuildId(install));
    }

    [Fact]
    public void Paths_LiveUnderTheAppDataFolder_AndTheExeMatchesTheOs()
    {
        Assert.Equal(_paths.GetAppDataPath("steamcmd"), _service.SteamCmdFolder);
        Assert.Equal(_paths.GetAppDataPath("dedicated-server"), _service.DefaultInstallFolder);
        Assert.Equal(Path.Join("/srv/vh", "valheim_server.x86_64"), _service.GetServerExePath("/srv/vh"));
        Assert.Null(_service.FindSteamCmd());
    }

    [Fact]
    public async Task InstallOrUpdate_RunsAnonymousAppUpdate_AndStreamsProgress()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell-script SteamCMD stand-in.");
        InstallFakeSteamCmd(
            " Update state (0x61) downloading, progress: 50.00 (1 / 2)",
            "Success! App '896660' fully installed.");
        var install = Path.Join(_root, "server");
        var progress = new ListProgress();

        await _service.InstallOrUpdateServerAsync(install, validate: true, progress, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "+force_install_dir", Path.GetFullPath(install), "+login", "anonymous", "+app_update", "896660", "validate", "+quit" },
            File.ReadAllLines(ArgsFile));
        Assert.Contains(progress.Reports, r => r.Percent == 50.0);
        Assert.True(Directory.Exists(install));
    }

    [Fact]
    public async Task InstallOrUpdate_WithoutSuccessLine_Fails_WithSteamCmdsError()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell-script SteamCMD stand-in.");
        InstallFakeSteamCmd("ERROR! Failed to install app '896660' (No subscription)");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.InstallOrUpdateServerAsync(Path.Join(_root, "server"), validate: false, null, TestContext.Current.CancellationToken));

        Assert.Contains("No subscription", e.Message);
        Assert.DoesNotContain("validate", File.ReadAllLines(ArgsFile));
    }

    // Right after SteamCMD updates itself, app_update often answers "Missing configuration"; running it again works.
    [Fact]
    public async Task InstallOrUpdate_RetriesWhileSteamCmdIsNotReady()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell-script SteamCMD stand-in.");
        InstallFlakySteamCmd(failures: 2, "ERROR! Failed to install app '896660' (Missing configuration)");
        var progress = new ListProgress();

        await _service.InstallOrUpdateServerAsync(Path.Join(_root, "server"), validate: false, progress, TestContext.Current.CancellationToken);

        Assert.Equal(3, Runs);
        Assert.Contains(progress.Reports, r => r.Line == string.Format(ValheimServerGUI.Localization.Strings.SteamCmd_Log_Retrying, 2, 3));
    }

    [Fact]
    public async Task InstallOrUpdate_GivesUpAfterThreeNotReadyRuns()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell-script SteamCMD stand-in.");
        InstallFlakySteamCmd(failures: 5, "ERROR! Failed to install app '896660' (Missing configuration)");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.InstallOrUpdateServerAsync(Path.Join(_root, "server"), validate: false, null, TestContext.Current.CancellationToken));

        Assert.Contains("Missing configuration", e.Message);
        Assert.Equal(SteamCmdService.InstallAttempts, Runs);
    }

    [Fact]
    public async Task InstallOrUpdate_DoesNotRetryOtherErrors()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell-script SteamCMD stand-in.");
        InstallFlakySteamCmd(failures: 1, "ERROR! Failed to install app '896660' (Disk write failure)");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.InstallOrUpdateServerAsync(Path.Join(_root, "server"), validate: false, null, TestContext.Current.CancellationToken));

        Assert.Equal(1, Runs);
    }

    [Fact]
    public async Task GetLatestBuildId_AsksForAppInfo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell-script SteamCMD stand-in.");
        InstallFakeSteamCmd("\"branches\"", "{", "\"public\"", "{", "\"buildid\" \"42\"", "}", "}");

        var latest = await _service.GetLatestBuildIdAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal("42", latest);
        Assert.Contains("+app_info_print", File.ReadAllLines(ArgsFile));
    }

    // First use downloads SteamCMD's tarball and unpacks it, keeping the launcher executable.
    [Fact]
    public async Task InstallOrUpdate_DownloadsSteamCmdFirst_WhenMissing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell-script SteamCMD stand-in.");
        var tarball = BuildTarGz("steamcmd.sh", ScriptBody("Success! App '896660' fully installed."));
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tarball) };

        await _service.InstallOrUpdateServerAsync(Path.Join(_root, "server"), validate: false, null, TestContext.Current.CancellationToken);

        Assert.EndsWith("steamcmd_linux.tar.gz", _http.Requests.Single().Request.RequestUri!.ToString());
        Assert.NotNull(_service.FindSteamCmd());
        // First a bare run so SteamCMD can update itself, then the install.
        var args = File.ReadAllLines(ArgsFile);
        Assert.Equal("+quit", args[0]);
        Assert.Contains("+app_update", args.Skip(1));
    }

    [Fact]
    public async Task Cancelling_StopsTheRun()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Uses a shell-script SteamCMD stand-in.");
        Directory.CreateDirectory(_service.SteamCmdFolder);
        var path = Path.Join(_service.SteamCmdFolder, "steamcmd.sh");
        File.WriteAllText(path, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.InstallOrUpdateServerAsync(Path.Join(_root, "server"), validate: false, null, cancel.Token));
    }

    [Fact]
    public void ExtractArchive_UnpacksZip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("steamcmd.exe").Open())) writer.Write("exe");
        buffer.Position = 0;
        var dest = Path.Join(_root, "zip");
        Directory.CreateDirectory(dest);

        SteamCmdService.ExtractArchive(buffer, dest, zip: true);

        Assert.Equal("exe", File.ReadAllText(Path.Join(dest, "steamcmd.exe")));
    }

    private static byte[] BuildTarGz(string name, string content)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
            };
            tar.WriteEntry(entry);
        }
        return output.ToArray();
    }
}
