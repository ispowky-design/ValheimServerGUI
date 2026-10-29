using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValheimServerGUI.App.Startup;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using Xunit;

namespace ValheimServerGUI.App.Tests.Startup;

/// <summary>Builds a <see cref="StartupService"/> over fakes: no disk, no network, no profiles unless given.</summary>
internal static class TestStartupService
{
    public static StartupService Create(
        FakeSoftwareUpdateProvider? update = null,
        FakePlayerDataRepository? players = null,
        UserPreferences? userPrefs = null,
        IEnumerable<ServerPreferences>? profiles = null)
        => new(update ?? new FakeSoftwareUpdateProvider(), players ?? new FakePlayerDataRepository(),
            new FakeUserPreferencesProvider(userPrefs), new FakeServerPreferencesProvider(profiles),
            new WorldBackupService(), new FakeSteamCmdService(Path.GetTempPath()),
            new StartupArgsProvider(Array.Empty<string>()), TestLog.Silent);
}

public class StartupServiceTests
{
    private sealed class ProgressRecorder : IProgress<StartupProgress>
    {
        public List<StartupProgress> Reports { get; } = new();
        public void Report(StartupProgress value) => Reports.Add(value);

        // Each step's last report: where it ended.
        public Dictionary<string, StartupProgress> Final => Reports.GroupBy(r => r.Title).ToDictionary(g => g.Key, g => g.Last());
    }

    // Paths that exist nowhere, so the fake user prefs never point at a real server on the test machine.
    private static UserPreferences NoServer(bool checkForUpdates = true) => new()
    {
        ServerExePath = Path.Join(Path.GetTempPath(), "vsg-no-server", "valheim_server"),
        SaveDataFolderPath = Path.Join(Path.GetTempPath(), "vsg-no-saves"),
        CheckForUpdates = checkForUpdates,
    };

    [Fact]
    public async Task RunAsync_checks_updates_and_loads_players_once()
    {
        var update = new FakeSoftwareUpdateProvider();
        var players = new FakePlayerDataRepository();

        await TestStartupService.Create(update, players, NoServer()).RunAsync();

        Assert.Equal(1, update.CheckCount);
        Assert.False(update.LastIsManual); // startup check is non-manual
        Assert.Equal(1, players.LoadCount); // roster hydrated exactly once
    }

    [Fact]
    public async Task RunAsync_lists_every_step_first_then_finishes_each_in_order()
    {
        var recorder = new ProgressRecorder();

        await TestStartupService.Create(userPrefs: NoServer()).RunAsync(recorder);

        var count = recorder.Reports[0].Count;
        Assert.True(count >= 8);
        // The whole list shows up front, all pending.
        Assert.All(recorder.Reports.Take(count), r => Assert.Equal(StartupStepState.Pending, r.State));
        Assert.Equal(Enumerable.Range(0, count), recorder.Reports.Take(count).Select(r => r.Index));
        // Then each step runs and finishes, one after the other.
        var rest = recorder.Reports.Skip(count).ToList();
        Assert.Equal(count * 2, rest.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal((i, StartupStepState.Running), (rest[2 * i].Index, rest[2 * i].State));
            Assert.Equal(i, rest[2 * i + 1].Index);
            Assert.True(rest[2 * i + 1].IsFinished);
        }
    }

    [Fact]
    public async Task RunAsync_reports_what_each_step_found()
    {
        var recorder = new ProgressRecorder();
        var profiles = new[]
        {
            new ServerPreferences { ProfileName = "A", LastSaved = DateTime.UtcNow.AddDays(-2) },
            new ServerPreferences { ProfileName = "B", LastSaved = DateTime.UtcNow },
        };
        var players = new FakePlayerDataRepository();
        players.PushUpdate(new PlayerInfo { Platform = "Steam", PlayerId = "1" });

        await TestStartupService.Create(players: players, userPrefs: NoServer(), profiles: profiles).RunAsync(recorder);

        var final = recorder.Final;
        Assert.Equal(StartupStepState.Done, final[Strings.Startup_Step_Profiles].State);
        Assert.Equal(string.Format(Strings.Startup_Profiles_Detail, string.Format(Strings.Startup_Profiles_Other, 2), "B"),
            final[Strings.Startup_Step_Profiles].Detail);
        Assert.Equal((StartupStepState.Warning, Strings.Startup_Server_Missing),
            (final[Strings.Startup_Step_Server].State, final[Strings.Startup_Step_Server].Detail));
        Assert.Equal((StartupStepState.Skipped, Strings.Startup_Plugin_NoServer),
            (final[Strings.Startup_Step_Plugin].State, final[Strings.Startup_Step_Plugin].Detail));
        Assert.Equal((StartupStepState.Skipped, Strings.Startup_World_None),
            (final[Strings.Startup_Step_World].State, final[Strings.Startup_Step_World].Detail));
        Assert.Equal(string.Format(Strings.Startup_Players_One, 1), final[Strings.Startup_Step_Players].Detail);
    }

    [Fact]
    public async Task RunAsync_skips_the_update_check_when_turned_off()
    {
        var recorder = new ProgressRecorder();
        var update = new FakeSoftwareUpdateProvider();

        await TestStartupService.Create(update, userPrefs: NoServer(checkForUpdates: false)).RunAsync(recorder);

        Assert.Equal(0, update.CheckCount);
        Assert.Equal((StartupStepState.Skipped, Strings.Startup_Updates_Off),
            (recorder.Final[Strings.Startup_Step_Updates].State, recorder.Final[Strings.Startup_Step_Updates].Detail));
    }

    [Fact]
    public async Task RunAsync_is_resilient_to_a_failing_task()
    {
        var recorder = new ProgressRecorder();
        var players = new FakePlayerDataRepository { ThrowOnLoad = true };
        var update = new FakeSoftwareUpdateProvider();

        await TestStartupService.Create(update, players, NoServer()).RunAsync(recorder); // must not throw

        Assert.Equal(1, update.CheckCount); // the update check still ran after the player load failed
        var failed = recorder.Final[Strings.Startup_Step_Players];
        Assert.Equal(StartupStepState.Warning, failed.State);
        Assert.Equal(string.Format(Strings.Startup_StepFailed, "load failed"), failed.Detail);
    }
}
