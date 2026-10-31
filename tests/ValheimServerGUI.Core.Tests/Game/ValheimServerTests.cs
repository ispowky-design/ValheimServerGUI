using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Serilog;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game;
using ValheimServerGUI.Tools.Data;
using ValheimServerGUI.Tools.Models;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game
{
    /// <summary>
    /// ValheimServer orchestration via a headless process provider. Status
    /// transitions, the GenerateArgs launch contract, the late-stop race, graceful-stop dispatch, and
    /// the restart choreography -- all with no real process, exe launch, or filesystem beyond a
    /// throwaway temp exe/savedir the validation requires.
    /// </summary>
    public class ValheimServerTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _exe;
        private readonly string _saveDir;
        private readonly MockProcessProvider _processProvider;
        private readonly PlayerDataRepository _repo;
        private readonly FakeApplicationLogger _appLogger = new();
        private readonly ValheimServer _server;

        public ValheimServerTests()
        {
            _dir = Path.Join(Path.GetTempPath(), "vsg-server-" + Guid.NewGuid().ToString("n"));
            _saveDir = Path.Join(_dir, "save");
            Directory.CreateDirectory(_saveDir);
            _exe = Path.Join(_dir, "valheim_server.x86_64");
            File.WriteAllText(_exe, ""); // validation only needs the file to exist

            var fileProvider = new MockDataFileProvider();
            ILogger serilog = new LoggerConfiguration().CreateLogger();
            var context = new DataFileRepositoryContext(fileProvider, serilog);
            var resolver = new LinuxValheimPathResolver("/tmp/vsg-test-home", xdgDataHome: null);
            _repo = new PlayerDataRepository(context, resolver);

            _processProvider = new MockProcessProvider();
            var accessLists = new PlayerAccessListService();
            var baselines = new InMemoryBaselineStore();
            _server = new ValheimServer(_processProvider, _repo, _appLogger, resolver, accessLists,
                new PlayerListImportService(accessLists, baselines), baselines);
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        private ValheimServerOptions Options(Action<ValheimServerOptions>? customize = null)
        {
            var options = new ValheimServerOptions
            {
                Name = "My Server",
                WorldName = "MyWorld",
                Password = "hunter2",
                Port = 2456,
                SaveInterval = 30,
                Backups = 1,
                BackupShort = 60,
                BackupLong = 120,
                ServerExePath = _exe,
                SaveDataFolderPath = _saveDir,
                LogToFile = false,
            };
            customize?.Invoke(options);
            return options;
        }

        private void FeedLog(string line) => _server.Logger!.Information(line);

        // Returns as soon as the condition holds; the timeout only bounds a real failure. It is generous because
        // the awaited work runs on the thread pool, which a loaded CI runner can be slow to schedule.
        private static void WaitFor(Func<bool> condition, int timeoutMs = 10000)
        {
            var sw = Stopwatch.StartNew();
            while (!condition() && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(10);
        }

        [Fact]
        public void IsStoppedInitially()
        {
            Assert.Equal(ServerStatus.Stopped, _server.Status);
            Assert.True(_server.CanStart);
            Assert.False(_server.CanStop);
        }

        [Fact]
        public void Start_MovesToStarting_AndBlocksReStart()
        {
            _server.Start(Options());

            Assert.Equal(ServerStatus.Starting, _server.Status);
            Assert.False(_server.CanStart);
            Assert.True(_server.CanStop);
        }

        // The GenerateArgs launch contract.
        [Fact]
        public void Start_EmitsExpectedLaunchArgs()
        {
            _server.Start(Options(o =>
            {
                o.Public = true;
                o.Crossplay = true;
            }));

            var args = _processProvider.LastProcess!.StartInfo.Arguments;

            Assert.Contains("-nographics -batchmode", args);
            Assert.Contains("-name \"My Server\"", args);
            Assert.Contains("-port 2456", args);
            Assert.Contains("-world \"MyWorld\"", args);
            Assert.Contains("-public 1", args);
            Assert.Contains("-password \"hunter2\"", args);
            Assert.Contains("-crossplay", args);
        }

        // The world stores its modifiers and keys and -modifier/-setkey only add to them, so the stored ones are
        // cleared first; otherwise a modifier set back to Normal (omitted) would keep the world's stored value.
        // Additional args come last so the user can still append their own world arguments.
        [Fact]
        public void Start_ClearsStoredWorldSettings_BeforeApplyingThem()
        {
            _server.Start(Options(o =>
            {
                o.WorldModifiers = new() { [WorldGenModifiers.Combat] = WorldGenModifiers.Values.CombatHard };
                o.WorldKeys = new() { WorldGenKeys.NoMap };
                o.AdditionalArgs = "-instanceid 1";
            }));

            var args = _processProvider.LastProcess!.StartInfo.Arguments;
            var reset = args.IndexOf(" -resetmodifiers", StringComparison.Ordinal);
            var modifier = args.IndexOf(" -modifier combat hard", StringComparison.Ordinal);
            var key = args.IndexOf(" -setkey nomap", StringComparison.Ordinal);
            var additional = args.IndexOf(" -instanceid 1", StringComparison.Ordinal);

            Assert.True(reset >= 0 && reset < modifier && modifier < key && key < additional, args);
            Assert.DoesNotContain("-preset", args);
        }

        [Fact]
        public void Start_ClearsStoredWorldSettings_WhenNoneAreConfigured()
        {
            _server.Start(Options());

            Assert.Contains(" -resetmodifiers", _processProvider.LastProcess!.StartInfo.Arguments);
        }

        // Linux launch contract: the server binary resolves steamclient.so from its own directory
        // (and linux64/ beneath it) and reads steam_appid.txt from the working directory, so Start
        // must set the working directory to the exe's folder and prepend that folder + linux64/ to
        // LD_LIBRARY_PATH. Without this the raw valheim_server.x86_64 fails to start on Linux.
        [Fact]
        public void Start_SetsWorkingDirectoryAndLibraryPath_ForServerLaunch()
        {
            _server.Start(Options());

            var startInfo = _processProvider.LastProcess!.StartInfo;

            Assert.Equal(CoreConstants.ValheimSteamAppId, startInfo.EnvironmentVariables["SteamAppId"]);
            Assert.Equal(_dir, startInfo.WorkingDirectory);

            // LD_LIBRARY_PATH is only set off-Windows; on Windows the DLL search handles this itself.
            if (!OperatingSystem.IsWindows())
            {
                var libPath = startInfo.EnvironmentVariables["LD_LIBRARY_PATH"];
                Assert.NotNull(libPath);
                Assert.Contains(_dir, libPath!.Split(':'));
                Assert.Contains(Path.Combine(_dir, "linux64"), libPath.Split(':'));
            }
        }

        private string CreateFile(params string[] relative)
        {
            var path = Path.Join(_dir, Path.Join(relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            return path;
        }

        // What ld.so preloads for this environment: LD_PRELOAD entries split on spaces and colons (no escaping), a name
        // with a slash used as a path, a bare name looked up in the LD_LIBRARY_PATH folders. Null for an entry that
        // does not resolve to a file, which ld.so reports as "cannot be preloaded" and skips.
        private static List<string?> PreloadsAsLdSoResolvesThem(StringDictionary env)
        {
            var libraryDirs = (env["LD_LIBRARY_PATH"] ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries);
            return env["LD_PRELOAD"]!.Split(new[] { ' ', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Contains('/')
                    ? (File.Exists(entry) ? entry : null)
                    : libraryDirs.Select(dir => Path.Join(dir, entry)).FirstOrDefault(File.Exists))
                .ToList();
        }

        // BepInEx on Linux: its Doorstop loader is a preload library that only loads when the launch environment
        // names it, so Start sets it up whenever both configured files exist. They need not be in the server folder,
        // and the folders may hold spaces like Steam's own "Valheim dedicated server". On Windows the pack's
        // winhttp.dll proxy loads by itself and Start must leave the environment alone.
        [Fact]
        public void Start_WithBothBepInExFiles_EnablesItsLoaderOnLinux()
        {
            var preloader = CreateFile("steamapps", "common", "Valheim dedicated server", "BepInEx", "core", "BepInEx.Preloader.dll");
            var doorstop = CreateFile("steamapps", "common", "Valheim dedicated server", "doorstop_libs", "libdoorstop_x64.so");

            _server.Start(Options(o =>
            {
                o.BepInExPreloaderPath = preloader;
                o.DoorstopLibraryPath = doorstop;
            }));

            var env = _processProvider.LastProcess!.StartInfo.EnvironmentVariables;
            if (OperatingSystem.IsWindows())
            {
                Assert.False(env.ContainsKey("DOORSTOP_ENABLED"));
                Assert.False(env.ContainsKey("DOORSTOP_TARGET_ASSEMBLY"));
            }
            else
            {
                Assert.Equal("1", env["DOORSTOP_ENABLED"]);
                Assert.Equal(preloader, env["DOORSTOP_TARGET_ASSEMBLY"]);
                var preloads = PreloadsAsLdSoResolvesThem(env);
                Assert.DoesNotContain(null, preloads);
                Assert.Equal(doorstop, preloads[0]);
                Assert.Contains(_appLogger.Messages, m => m.Contains("Loading BepInEx") && m.Contains(preloader));
            }
        }

        // Either file missing: start vanilla, and say which file was missing.
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(false, false)]
        public void Start_WithABepInExFileMissing_LeavesItsLoaderOff_AndLogsTheMissingFile(bool hasPreloader, bool hasDoorstop)
        {
            var preloader = Path.Join(_dir, "BepInEx", "core", "BepInEx.Preloader.dll");
            var doorstop = Path.Join(_dir, "doorstop_libs", "libdoorstop_x64.so");
            if (hasPreloader) CreateFile("BepInEx", "core", "BepInEx.Preloader.dll");
            if (hasDoorstop) CreateFile("doorstop_libs", "libdoorstop_x64.so");

            _server.Start(Options(o =>
            {
                o.BepInExPreloaderPath = preloader;
                o.DoorstopLibraryPath = doorstop;
            }));

            var env = _processProvider.LastProcess!.StartInfo.EnvironmentVariables;
            Assert.False(env.ContainsKey("DOORSTOP_ENABLED"));
            Assert.False(env.ContainsKey("DOORSTOP_TARGET_ASSEMBLY"));
            if (!OperatingSystem.IsWindows())
            {
                var missing = hasPreloader ? doorstop : preloader;
                Assert.Contains(_appLogger.Messages, m => m.Contains("BepInEx not loaded") && m.Contains(missing));
            }
        }

        // The profile's variables reach the process. The path lists keep the profile's value, with the entries the
        // launch needs placed in front of it.
        [Fact]
        public void Start_AppliesProfileEnvironmentVariables_UnderTheLaunchRequirements()
        {
            var preloader = CreateFile("BepInEx", "core", "BepInEx.Preloader.dll");
            var doorstop = CreateFile("doorstop_libs", "libdoorstop_x64.so");

            _server.Start(Options(o =>
            {
                o.BepInExPreloaderPath = preloader;
                o.DoorstopLibraryPath = doorstop;
                o.EnvironmentVariables = @"MY_FLAG=1 MY_PATH=""/opt/my dir"" LD_PRELOAD=/opt/libmine.so LD_LIBRARY_PATH=/opt/lib";
            }));

            var env = _processProvider.LastProcess!.StartInfo.EnvironmentVariables;
            Assert.Equal("1", env["MY_FLAG"]);
            Assert.Equal("/opt/my dir", env["MY_PATH"]);
            Assert.Equal(CoreConstants.ValheimSteamAppId, env[ValheimServer.SteamAppIdVariable]);
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal("/opt/libmine.so", env["LD_PRELOAD"]);
                Assert.Equal("/opt/lib", env["LD_LIBRARY_PATH"]);
            }
            else
            {
                Assert.Equal(new[] { Path.GetFileName(doorstop), "/opt/libmine.so" }, env["LD_PRELOAD"]!.Split(':'));
                Assert.Equal(new[] { Path.GetDirectoryName(doorstop), _dir, Path.Join(_dir, "linux64"), "/opt/lib" },
                    env["LD_LIBRARY_PATH"]!.Split(':'));
            }
        }

        // The run-command log line shows the variables as typed, ahead of the command, and still masks the password.
        [Fact]
        public void Start_LogsTheRunCommand_WithEnvironmentVariablesFirst()
        {
            _server.Start(Options(o => o.EnvironmentVariables = @"  MY_FLAG=1 MY_PATH=""/opt/my dir""  "));

            var line = Assert.Single(_appLogger.Messages, m => m.StartsWith("Server run command:"));
            // The test logger renders string properties quoted; the app's sink renders them as-is.
            Assert.StartsWith(@"Server run command: MY_FLAG=1 MY_PATH=""/opt/my dir"" """, line);
            Assert.Contains(_exe, line);
            Assert.DoesNotContain("hunter2", line);
        }

        [Fact]
        public void Start_WithMalformedEnvironmentVariables_DoesNotStart()
        {
            Assert.Throws<ArgumentException>(() => _server.Start(Options(o => o.EnvironmentVariables = "NOT_AN_ASSIGNMENT")));

            Assert.Null(_processProvider.LastProcess);
            Assert.Equal(ServerStatus.Stopped, _server.Status);
        }

        [Fact]
        public void ConnectedLogLine_PromotesToRunning()
        {
            _server.Start(Options());

            FeedLog("Game server connected");

            Assert.Equal(ServerStatus.Running, _server.Status);
            Assert.True(_server.CanRestart);
        }

        [Fact]
        public void Stop_DispatchesGracefulKill_AndEntersStopping()
        {
            _server.Start(Options());

            _server.Stop();

            Assert.Equal(ServerStatus.Stopping, _server.Status);
            Assert.Single(_processProvider.SafelyKilledKeys);
        }

        // Stop during startup: a late "connected" must not promote Stopping -> Running.
        [Fact]
        public void LateConnected_AfterStop_StaysStopping()
        {
            _server.Start(Options());
            _server.Stop();
            Assert.Equal(ServerStatus.Stopping, _server.Status);

            FeedLog("Game server connected");

            Assert.Equal(ServerStatus.Stopping, _server.Status);
        }

        // The process dies unexpectedly -> Stopped, no auto-restart.
        [Fact]
        public void ProcessExit_MovesToStopped_AndAllowsRestart()
        {
            _server.Start(Options());

            _processProvider.SimulateExit();

            Assert.Equal(ServerStatus.Stopped, _server.Status);
            Assert.True(_server.CanStart);
        }

        [Fact]
        public void Restart_WhenRunning_DispatchesKill_AndEntersStopping()
        {
            _server.Start(Options());
            FeedLog("Game server connected");
            Assert.Equal(ServerStatus.Running, _server.Status);

            _server.Restart();

            Assert.Equal(ServerStatus.Stopping, _server.Status);
            Assert.Single(_processProvider.SafelyKilledKeys);
        }

        // A restart re-starts the server once the old process has exited.
        [Fact]
        public void Restart_AfterProcessExits_StartsAgain()
        {
            _server.Start(Options());
            FeedLog("Game server connected");
            _server.Restart();

            _processProvider.SimulateExit();

            // The Stopped handler waits ~500ms before re-starting.
            WaitFor(() => _server.Status == ServerStatus.Starting);
            Assert.Equal(ServerStatus.Starting, _server.Status);
        }

        // Force stop is a last resort for a stop that will not finish: only offered while Stopping, never sent by
        // Stop itself, and it kills (no graceful signal) the tracked process.
        [Fact]
        public void ForceStop_OnlyWhileStopping_KillsTheProcess()
        {
            _server.Start(Options());
            FeedLog("Game server connected");

            _server.ForceStop();
            Assert.Empty(_processProvider.ForceKilledKeys);
            Assert.False(_server.CanForceStop);

            _server.Stop();
            Assert.True(_server.CanForceStop);
            Assert.Empty(_processProvider.ForceKilledKeys);

            _server.ForceStop();
            Assert.Equal(_processProvider.SafelyKilledKeys, _processProvider.ForceKilledKeys);

            _processProvider.SimulateExit();
            Assert.Equal(ServerStatus.Stopped, _server.Status);
        }

        // Killing a restart that is stuck stopping must not start the server again on exit.
        [Fact]
        public void ForceStop_DuringRestart_CancelsTheRestart()
        {
            _server.Start(Options());
            FeedLog("Game server connected");
            _server.Restart();

            _server.ForceStop();
            _processProvider.SimulateExit();

            Thread.Sleep(800); // past the ~500ms the restart path waits before starting again
            Assert.Equal(ServerStatus.Stopped, _server.Status);
        }

        // ---- scheduled restarts ----

        [Fact]
        public void NextScheduledRestart_IsSetWhileRunning_FromTheSchedule()
        {
            _server.Clock = () => new DateTime(2026, 10, 26, 12, 0, 0);
            _server.Start(Options(o => o.ScheduledRestartTimes = "06:00, 18:00"));
            Assert.Null(_server.NextScheduledRestart); // Starting

            FeedLog("Game server connected");
            Assert.Equal(new DateTime(2026, 10, 26, 18, 0, 0), _server.NextScheduledRestart);

            _server.Stop();
            Assert.Null(_server.NextScheduledRestart);
        }

        [Fact]
        public void NextScheduledRestart_IsNullWithoutASchedule()
        {
            _server.Start(Options());
            FeedLog("Game server connected");

            Assert.Null(_server.NextScheduledRestart);
        }

        [Fact]
        public void ScheduledRestart_RunsTheStoppedTask_ThenStartsAgain()
        {
            _server.Start(Options());
            FeedLog("Game server connected");
            var statusDuringTask = (ServerStatus?)null;

            _server.ScheduledRestart(_ => statusDuringTask = _server.Status);
            _processProvider.SimulateExit();

            WaitFor(() => _server.Status == ServerStatus.Starting);
            Assert.Equal(ServerStatus.Stopped, statusDuringTask);
            Assert.Equal(ServerStatus.Starting, _server.Status);
        }

        [Fact]
        public void ScheduledRestart_StartsAgainWhenTheStoppedTaskFails()
        {
            _server.Start(Options());
            FeedLog("Game server connected");

            _server.ScheduledRestart(_ => throw new IOException("disk full"));
            _processProvider.SimulateExit();

            WaitFor(() => _server.Status == ServerStatus.Starting);
            Assert.Equal(ServerStatus.Starting, _server.Status);
            Assert.Contains(_appLogger.Messages, m => m.Contains("disk full"));
        }

        [Fact]
        public void ForceStop_DuringAScheduledRestart_DropsTheStoppedTask()
        {
            _server.Start(Options());
            FeedLog("Game server connected");
            var ran = false;

            _server.ScheduledRestart(_ => ran = true);
            _server.ForceStop();
            _processProvider.SimulateExit();

            Thread.Sleep(800);
            Assert.False(ran);
            Assert.Equal(ServerStatus.Stopped, _server.Status);
        }

        [Fact]
        public void ScheduledRestartService_RestartsADueServer_AndBacksUpItsWorld()
        {
            var world = Directory.CreateDirectory(Path.Join(_saveDir, "worlds_local", "MyWorld"));
            File.WriteAllText(Path.Join(world.FullName, "_main.1.fwl2"), "w");
            _server.Clock = () => new DateTime(2026, 10, 26, 5, 0, 0);
            _server.Start(Options(o =>
            {
                o.ScheduledRestartTimes = "06:00";
                o.BackupOnScheduledRestart = true;
            }));
            FeedLog("Game server connected");
            var backups = new WorldBackupService();
            var service = new ScheduledRestartService(new SingleServerManager(_server), backups, _appLogger);

            service.CheckDue(new DateTime(2026, 10, 26, 5, 59, 0));
            Assert.Equal(ServerStatus.Running, _server.Status); // not yet due

            service.CheckDue(new DateTime(2026, 10, 26, 6, 0, 10));
            Assert.Equal(ServerStatus.Stopping, _server.Status);
            _processProvider.SimulateExit();

            WaitFor(() => _server.Status == ServerStatus.Starting);
            var backup = Assert.Single(backups.GetBackups(new DirectoryInfo(_saveDir), "MyWorld"));
            Assert.Equal(WorldBackupKind.Scheduled, backup.Kind);
        }

        [Fact]
        public void ScheduledRestartService_SkipsTheBackupWhenTheProfileTurnsItOff()
        {
            var world = Directory.CreateDirectory(Path.Join(_saveDir, "worlds_local", "MyWorld"));
            File.WriteAllText(Path.Join(world.FullName, "_main.1.fwl2"), "w");
            _server.Clock = () => new DateTime(2026, 10, 26, 5, 0, 0);
            _server.Start(Options(o => o.ScheduledRestartTimes = "06:00"));
            FeedLog("Game server connected");
            var backups = new WorldBackupService();

            new ScheduledRestartService(new SingleServerManager(_server), backups, _appLogger).CheckDue(new DateTime(2026, 10, 26, 6, 0, 0));
            _processProvider.SimulateExit();

            WaitFor(() => _server.Status == ServerStatus.Starting);
            Assert.Empty(backups.GetBackups(new DirectoryInfo(_saveDir), "MyWorld"));
        }

        // ---- crash restarts ----

        [Fact]
        public void Crash_WhileRunning_SchedulesARestart_AndRaisesCrashed()
        {
            _server.Clock = () => new DateTime(2026, 10, 31, 12, 0, 0);
            _server.Start(Options(o => o.RestartOnCrash = true));
            FeedLog("Game server connected");
            ServerCrash? crash = null;
            _server.Crashed += (_, c) => crash = c;

            _processProvider.SimulateExit();

            Assert.Equal(ServerStatus.Stopped, _server.Status);
            Assert.Equal(new DateTime(2026, 10, 31, 12, 0, 10), _server.CrashRestartDueAt);
            Assert.NotNull(crash);
            Assert.Equal(1, crash!.ConsecutiveCrashes);
            Assert.Equal(_server.CrashRestartDueAt, crash.RestartDueAt);
        }

        [Fact]
        public void Crash_WhileStarting_CountsAsACrash()
        {
            _server.Start(Options(o => o.RestartOnCrash = true));

            _processProvider.SimulateExit();

            Assert.NotNull(_server.CrashRestartDueAt);
        }

        [Fact]
        public void Crash_WithRestartOff_RaisesCrashed_ButSchedulesNothing()
        {
            _server.Start(Options(o => o.RestartOnCrash = false));
            FeedLog("Game server connected");
            ServerCrash? crash = null;
            _server.Crashed += (_, c) => crash = c;

            _processProvider.SimulateExit();

            Assert.NotNull(crash);
            Assert.Null(crash!.RestartDueAt);
            Assert.Null(_server.CrashRestartDueAt);
        }

        [Fact]
        public void Stop_ThenExit_IsNotACrash()
        {
            _server.Start(Options(o => o.RestartOnCrash = true));
            FeedLog("Game server connected");
            var crashed = false;
            _server.Crashed += (_, _) => crashed = true;

            _server.Stop();
            _processProvider.SimulateExit();

            Assert.False(crashed);
            Assert.Null(_server.CrashRestartDueAt);
        }

        [Fact]
        public void Restart_ThenExit_IsNotACrash()
        {
            _server.Start(Options(o => o.RestartOnCrash = true));
            FeedLog("Game server connected");
            var crashed = false;
            _server.Crashed += (_, _) => crashed = true;

            _server.Restart();
            _processProvider.SimulateExit();

            WaitFor(() => _server.Status == ServerStatus.Starting);
            Assert.False(crashed);
        }

        [Fact]
        public void CrashRestarts_WaitLonger_ThenGiveUpAfterThree()
        {
            var now = new DateTime(2026, 10, 31, 12, 0, 0);
            _server.Clock = () => now;
            _server.Start(Options(o => o.RestartOnCrash = true));

            var delays = new List<TimeSpan?>();
            for (var i = 0; i < 4; i++)
            {
                _processProvider.SimulateExit();
                delays.Add(_server.CrashRestartDueAt - now);
                _server.RestartAfterCrash(null);
            }

            Assert.Equal(new TimeSpan?[] { TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(90), null }, delays);
            Assert.Equal(4, _server.ConsecutiveCrashes);
            Assert.Equal(ServerStatus.Stopped, _server.Status);
        }

        [Fact]
        public void ManualStart_ClearsTheCrashStreak_AndThePendingRestart()
        {
            _server.Start(Options(o => o.RestartOnCrash = true));
            _processProvider.SimulateExit();
            Assert.NotNull(_server.CrashRestartDueAt);

            _server.Start(Options(o => o.RestartOnCrash = true));

            Assert.Null(_server.CrashRestartDueAt);
            Assert.Equal(0, _server.ConsecutiveCrashes);
            Assert.Equal(ServerStatus.Starting, _server.Status);
        }

        [Fact]
        public void CancelCrashRestart_LeavesTheServerStopped()
        {
            _server.Start(Options(o => o.RestartOnCrash = true));
            _processProvider.SimulateExit();

            _server.CancelCrashRestart();
            _server.RestartAfterCrash(null);

            Assert.Null(_server.CrashRestartDueAt);
            Assert.Equal(ServerStatus.Stopped, _server.Status);
        }

        [Fact]
        public void CrashRestartService_StartsADueServer_AndBacksUpItsWorld()
        {
            var world = Directory.CreateDirectory(Path.Join(_saveDir, "worlds_local", "MyWorld"));
            File.WriteAllText(Path.Join(world.FullName, "_main.1.fwl2"), "w");
            _server.Clock = () => new DateTime(2026, 10, 31, 12, 0, 0);
            _server.Start(Options(o => o.RestartOnCrash = true));
            FeedLog("Game server connected");
            _processProvider.SimulateExit();
            var backups = new WorldBackupService();
            var service = new CrashRestartService(new SingleServerManager(_server), backups, _appLogger);

            service.CheckDue(new DateTime(2026, 10, 31, 12, 0, 5));
            Assert.Equal(ServerStatus.Stopped, _server.Status); // not yet due

            service.CheckDue(new DateTime(2026, 10, 31, 12, 0, 10));
            Assert.Equal(ServerStatus.Starting, _server.Status);
            var backup = Assert.Single(backups.GetBackups(new DirectoryInfo(_saveDir), "MyWorld"));
            Assert.Equal(WorldBackupKind.Scheduled, backup.Kind);
        }

        private sealed class SingleServerManager : IServerManager
        {
            private readonly ValheimServer _server;
            public SingleServerManager(ValheimServer server) => _server = server;
            public IReadOnlyCollection<ValheimServer> All => new[] { _server };
            public ValheimServer GetOrCreate(string profileName) => _server;
            public bool TryGet(string profileName, out ValheimServer server) { server = _server; return true; }
            public System.Collections.ObjectModel.ObservableCollection<string> GetServerLog(string profileName) => new();
            public Action<string> GetLogAppender(string profileName) => _ => { };
            public void Remove(string profileName) { }
            public void StopAllAndDispose() { }
            public event EventHandler<PlayerRolesAdoptedEventArgs>? PlayerRolesAdopted { add { } remove { } }
        }

        [Fact]
        public void StoppingSince_IsStampedOnStopping_AndClearedAfter()
        {
            _server.Start(Options());
            Assert.Null(_server.StoppingSince);

            _server.Stop();
            Assert.NotNull(_server.StoppingSince);

            _processProvider.SimulateExit();
            Assert.Null(_server.StoppingSince);
        }

        // StartedAt derives uptime from the server's own Running transition (not a per-window capture), and
        // clears once fully Stopped so a re-target onto a stopped profile reads no uptime.
        [Fact]
        public void StartedAt_IsStampedOnRunning_AndClearedOnStopped()
        {
            Assert.Null(_server.StartedAt);

            _server.Start(Options());
            Assert.Null(_server.StartedAt); // Starting, not yet Running

            FeedLog("Game server connected");
            Assert.NotNull(_server.StartedAt);

            _processProvider.SimulateExit(); // → Stopped
            Assert.Null(_server.StartedAt);
        }

        private static Dictionary<string, PlayerRoleEntry> Overrides(params (string id, PlayerRole role)[] roles)
        {
            var result = new Dictionary<string, PlayerRoleEntry>();
            foreach (var (id, role) in roles) result[$"Steam:{id}"] = new PlayerRoleEntry(role, "Steam");
            return result;
        }

        private string[] ListLines(string file) => File.ReadAllLines(Path.Join(_saveDir, file));

        // The start path writes the profile's roles to the three gating files (one hook covers manual / auto /
        // restart). Here: permitted-list mode puts the admin on both adminlist and permittedlist, and the ban list
        // is header-only (ignored in this mode).
        [Fact]
        public void Start_WritesAccessListsFromOptions()
        {
            _server.Start(Options(o =>
            {
                o.UsePermittedList = true;
                o.RoleOverrides = Overrides(("111", PlayerRole.Admin), ("222", PlayerRole.Banned));
            }));

            Assert.Contains("Steam_111", ListLines("adminlist.txt"));
            Assert.Contains("Steam_111", ListLines("permittedlist.txt")); // admin must also be permitted to join
            Assert.DoesNotContain("Steam_222", ListLines("bannedlist.txt")); // ban list unused in permitted mode
            Assert.Single(ListLines("bannedlist.txt"));                      // header only
        }

        // Effective roles are resolved from the overrides over the global defaults: a default reaches the files
        // unless this server overrides it.
        [Fact]
        public void Start_ResolvesOverridesOverDefaults()
        {
            _server.Start(Options(o =>
            {
                o.RoleOverrides = Overrides(("111", PlayerRole.Permitted));
                o.RoleDefaults = new Dictionary<string, PlayerDefaultEntry>
                {
                    ["Steam:111"] = new(PlayerRole.Admin, "Steam"),
                    ["Steam:222"] = new(PlayerRole.Admin, "Steam"),
                };
            }));

            Assert.DoesNotContain("Steam_111", ListLines("adminlist.txt")); // overridden
            Assert.Contains("Steam_222", ListLines("adminlist.txt"));       // default applies
        }

        // An entry the profile doesn't have is dropped from the file, but only after the file is backed up.
        [Fact]
        public void Start_EntryNotInTheProfile_IsBackedUpBeforeItIsDropped()
        {
            File.WriteAllText(Path.Join(_saveDir, "adminlist.txt"), "// header\nSteam_76561198000000999\n");

            _server.Start(Options());

            Assert.DoesNotContain("Steam_76561198000000999", ListLines("adminlist.txt"));
            Assert.Contains("Steam_76561198000000999", ListLines("adminlist.bak.txt"));
        }

        // Applying roles to an already-running server rewrites the files now AND updates the
        // live options, so a restart (which reuses Options) keeps the change instead of reverting.
        [Fact]
        public void ApplyPlayerRoles_RewritesFiles_AndUpdatesLiveOptions()
        {
            _server.Start(Options()); // started with no roles -> header-only files
            Assert.Single(ListLines("adminlist.txt"));

            var overrides = Overrides(("500", PlayerRole.Admin));
            _server.ApplyPlayerRoles(overrides, new Dictionary<string, PlayerDefaultEntry>(), usePermittedList: false);

            Assert.Contains("Steam_500", ListLines("adminlist.txt"));
            Assert.Same(overrides, _server.Options.RoleOverrides);
        }

        // A change made to the files during play (here: an in-game ban) is adopted before a live role change
        // rewrites them, and the owner is told so it can save the adopted roles to the profile.
        [Fact]
        public void ApplyPlayerRoles_AdoptsChangesMadeDuringPlay_AndRaisesPlayerRolesAdopted()
        {
            _server.Start(Options());
            FeedLog("Game server connected");
            File.AppendAllText(Path.Join(_saveDir, "bannedlist.txt"), "Steam_76561198000000777\n");
            ReconcileResult? adopted = null;
            _server.PlayerRolesAdopted += (_, r) => adopted = r;

            _server.ApplyPlayerRoles(Overrides(("500", PlayerRole.Admin)), new Dictionary<string, PlayerDefaultEntry>(), false);

            Assert.Contains("Steam_76561198000000777", ListLines("bannedlist.txt"));
            Assert.NotNull(adopted);
            Assert.Equal(PlayerRole.Banned, adopted!.Overrides["Steam:76561198000000777"].Role);
            Assert.Equal(PlayerRole.Admin, adopted.Overrides["Steam:500"].Role); // the live change itself is kept
        }

        [Fact]
        public void Restart_AdoptsChangesMadeDuringPlay()
        {
            _server.Start(Options());
            FeedLog("Game server connected");
            File.AppendAllText(Path.Join(_saveDir, "bannedlist.txt"), "Steam_76561198000000777\n");
            ReconcileResult? adopted = null;
            _server.PlayerRolesAdopted += (_, r) => adopted = r;

            _server.Restart();
            _processProvider.SimulateExit();
            WaitFor(() => _server.Status == ServerStatus.Starting);

            Assert.Contains("Steam_76561198000000777", ListLines("bannedlist.txt"));
            Assert.NotNull(adopted);
            Assert.Equal(PlayerRole.Banned, _server.Options.RoleOverrides["Steam:76561198000000777"].Role);
        }

        // A plain start does NOT adopt (the view-model reconciles first, asking the user about conflicts): an
        // unknown entry is dropped, backed up — the profile wins.
        [Fact]
        public void Start_DoesNotAdopt_ItBacksUpInstead()
        {
            File.WriteAllText(Path.Join(_saveDir, "bannedlist.txt"), "// header\nSteam_76561198000000777\n");
            var raised = false;
            _server.PlayerRolesAdopted += (_, _) => raised = true;

            _server.Start(Options());

            Assert.False(raised);
            Assert.DoesNotContain("Steam_76561198000000777", ListLines("bannedlist.txt"));
            Assert.Contains("Steam_76561198000000777", ListLines("bannedlist.bak.txt"));
        }

        // A list backup that fails stops the launch before anything starts.
        [Fact]
        public void Start_WhenAListBackupFails_DoesNotStart()
        {
            File.WriteAllText(Path.Join(_saveDir, "adminlist.txt"), "// header\nSteam_76561198000000999\n");
            Directory.CreateDirectory(Path.Join(_saveDir, "adminlist.bak.txt"));
            for (var i = 2; i <= 5; i++) Directory.CreateDirectory(Path.Join(_saveDir, $"adminlist.bak.{i}.txt"));

            Assert.Throws<PlayerListBackupException>(() => _server.Start(Options()));

            Assert.Equal(ServerStatus.Stopped, _server.Status);
            Assert.True(_server.CanStart);
        }

        [Fact]
        public void WorldSavedLogLine_ReRaisesWorldSavedEvent()
        {
            decimal? saved = null;
            _server.WorldSaved += (_, ms) => saved = ms;
            _server.Start(Options());

            FeedLog("World saved ( 10.5ms )");

            Assert.Equal(10.5m, saved);
        }

        // Player names from the world save: read at start, reread after each save, and never a reason not to start.

        private DirectoryInfo WorldFolder => new DirectoryInfo(_saveDir).GetLocalWorldFolder("MyWorld");

        private static byte[] SaveNaming(params (string Id, string Name)[] players)
            => WorldSaveFixtures.Build(players.Select(p => new WorldPlayerHistoryEntry(p.Id, p.Name, p.Name, "")));

        private PlayerInfo KnownPlayer(string platform, string playerId)
        {
            var player = new PlayerInfo { Platform = platform, PlayerId = playerId };
            _repo.Upsert(player);
            return player;
        }

        [Fact]
        public void Start_NamesKnownPlayersFromTheWorldSave()
        {
            var player = KnownPlayer(PlayerPlatforms.Steam, "76561198000000001");
            WorldSaveFixtures.WriteSave(WorldFolder, 3, SaveNaming(("Steam_76561198000000001", "Viking")));

            _server.Start(Options());

            Assert.Equal("Viking", player.PlayerName);
        }

        [Fact]
        public void WorldSaveCompleted_RereadsTheWorldSave()
        {
            var steam = KnownPlayer(PlayerPlatforms.Steam, "76561198000000001");
            var xbox = KnownPlayer(PlayerPlatforms.Xbox, "2533274900000001");
            WorldSaveFixtures.WriteSave(WorldFolder, 3, SaveNaming(("Steam_76561198000000001", "Viking")));
            _server.Start(Options());
            Assert.Null(xbox.PlayerName);

            // The Xbox player joined mid-session; the next save records them.
            WorldSaveFixtures.WriteSave(WorldFolder, 4, SaveNaming(
                ("Steam_76561198000000001", "Viking"),
                ("Xbox_2533274900000001", "Shieldmaiden1")));
            FeedLog("World save (5/5) done. Total time [20ms]");

            Assert.Equal("Viking", steam.PlayerName);
            Assert.Equal("Shieldmaiden1", xbox.PlayerName);
        }

        [Fact]
        public void Start_WithAnUnreadableWorldSave_StillStarts_AndLogsAWarning()
        {
            WorldSaveFixtures.WriteSave(WorldFolder, 3, new byte[] { 0xFF, 0xFF });

            _server.Start(Options());

            Assert.Equal(ServerStatus.Starting, _server.Status);
            Assert.Contains(_appLogger.Messages, m => m.Contains("Could not read player names"));
        }

        [Fact]
        public void Start_WithNoWorldSaveYet_StillStarts()
        {
            _server.Start(Options());

            Assert.Equal(ServerStatus.Starting, _server.Status);
            Assert.DoesNotContain(_appLogger.Messages, m => m.Contains("Could not read player names"));
        }

        // World settings from the save: -resetmodifiers clears every stored key, so the stored keys VSG does not own
        // (console keys, mods, newer game versions) are set again after VSG's own; VSG's own are replaced.
        [Fact]
        public void Start_CarriesOverStoredKeysVsgDoesNotOwn()
        {
            var stored = new[] { "nocraftcost", "somefuturekey 50", "enemydamage 150", "preset combat_hard" };
            WorldSaveFixtures.WriteSave(WorldFolder, 3, WorldSaveFixtures.Build(Array.Empty<WorldPlayerHistoryEntry>(), globalKeys: stored));

            _server.Start(Options(o => o.WorldKeys = new() { WorldGenKeys.NoMap }));

            var args = _processProvider.LastProcess!.StartInfo.Arguments;
            var reset = args.IndexOf(" -resetmodifiers", StringComparison.Ordinal);
            var vsgKey = args.IndexOf(" -setkey nomap", StringComparison.Ordinal);
            var carried = args.IndexOf(@" -setkey ""nocraftcost""", StringComparison.Ordinal);
            Assert.True(reset >= 0 && reset < vsgKey && vsgKey < carried, args);
            Assert.Contains(@" -setkey ""somefuturekey 50""", args);
            Assert.DoesNotContain("enemydamage", args);
            Assert.DoesNotContain("preset", args);
        }

        // Without the stored keys VSG cannot carry them over, so it leaves them as they are rather than clear them.
        [Fact]
        public void Start_WithAnUnreadableWorldSave_LeavesStoredSettingsAlone()
        {
            WorldSaveFixtures.WriteSave(WorldFolder, 3, new byte[] { 0xFF, 0xFF });

            _server.Start(Options());

            Assert.DoesNotContain("-resetmodifiers", _processProvider.LastProcess!.StartInfo.Arguments);
        }

        [Fact]
        public void Start_WithOnlyAPre10WorldSave_LeavesStoredSettingsAlone()
        {
            Directory.CreateDirectory(Path.Join(_saveDir, "worlds"));
            File.WriteAllBytes(Path.Join(_saveDir, "worlds", "MyWorld.fwl"), new byte[] { 1, 2, 3 });

            _server.Start(Options());

            Assert.DoesNotContain("-resetmodifiers", _processProvider.LastProcess!.StartInfo.Arguments);
        }
    }
}
