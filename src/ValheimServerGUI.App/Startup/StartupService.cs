using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using ValheimServerGUI.App.Converters;
using ValheimServerGUI.Game;
using ValheimServerGUI.Game.WorldMap;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;

namespace ValheimServerGUI.App.Startup;

/// <summary>
/// Runs the startup steps shown on the splash screen before the main windows open, one at a time, reporting each
/// step's state and what it found: preferences, server profiles, the dedicated server, the player positions plugin,
/// the world, its backups, the player roster (<see cref="IPlayerDataRepository.LoadAsync"/> MUST be called once at
/// startup) and the update check. Every step only reads: nothing here changes a server, its files or its world.
/// Each step is isolated so a failure (e.g. no network for the update check) shows on its line and the launch goes on.
/// </summary>
public sealed class StartupService
{
    private readonly ISoftwareUpdateProvider _updateProvider;
    private readonly IPlayerDataRepository _playerData;
    private readonly IUserPreferencesProvider _userPrefs;
    private readonly IServerPreferencesProvider _serverPrefs;
    private readonly IWorldBackupService _backups;
    private readonly ISteamCmdService _steamCmd;
    private readonly IStartupArgsProvider _startupArgs;
    private readonly ILogger _logger;

    public StartupService(
        ISoftwareUpdateProvider updateProvider,
        IPlayerDataRepository playerData,
        IUserPreferencesProvider userPrefs,
        IServerPreferencesProvider serverPrefs,
        IWorldBackupService backups,
        ISteamCmdService steamCmd,
        IStartupArgsProvider startupArgs,
        ILogger logger)
    {
        _updateProvider = updateProvider;
        _playerData = playerData;
        _userPrefs = userPrefs;
        _serverPrefs = serverPrefs;
        _backups = backups;
        _steamCmd = steamCmd;
        _startupArgs = startupArgs;
        _logger = logger;
    }

    private readonly record struct StepResult(StartupStepState State, string? Detail = null);

    private sealed record Step(string Name, string Title, Func<StartupContext, Task<StepResult>> Run);

    // What the earlier steps found, for the later ones.
    private sealed class StartupContext
    {
        public UserPreferences UserPrefs { get; set; } = UserPreferences.GetDefault();
        public ServerPreferences? Profile { get; set; }
        public string? ServerExe { get; set; }
        public DirectoryInfo? SaveFolder { get; set; }
        public string? World { get; set; }
    }

    public async Task RunAsync(IProgress<StartupProgress>? progress = null)
    {
        var steps = new List<Step>
        {
            new("preferences", Strings.Startup_Step_Preferences, c => OffUi(() => LoadPreferences(c))),
            new("profiles", Strings.Startup_Step_Profiles, c => OffUi(() => LoadProfiles(c))),
            new("server", Strings.Startup_Step_Server, c => OffUi(() => FindServer(c))),
            new("plugin", Strings.Startup_Step_Plugin, c => OffUi(() => CheckPlugin(c))),
            new("world", Strings.Startup_Step_World, c => OffUi(() => ReadWorld(c))),
            new("backups", Strings.Startup_Step_Backups, c => OffUi(() => CountBackups(c))),
            new("player-data load", Strings.Startup_Step_Players, _ => LoadPlayersAsync()),
            new("update check", Strings.Startup_Step_Updates, CheckForUpdatesAsync),
        };

        // Every line shows from the start, so the user sees what is coming.
        for (var i = 0; i < steps.Count; i++)
            progress?.Report(new StartupProgress(i, steps.Count, steps[i].Title, StartupStepState.Pending));

        var context = new StartupContext();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            progress?.Report(new StartupProgress(i, steps.Count, step.Title, StartupStepState.Running));
            var result = await RunSafelyAsync(step.Name, () => step.Run(context));
            progress?.Report(new StartupProgress(i, steps.Count, step.Title, result.State, result.Detail));
        }
    }

    // The file reads are quick but synchronous: off the UI thread, the splash keeps painting meanwhile.
    private static Task<StepResult> OffUi(Func<StepResult> step) => Task.Run(step);

    private StepResult LoadPreferences(StartupContext context)
    {
        context.UserPrefs = _userPrefs.LoadPreferences();
        return new StepResult(StartupStepState.Done, string.Format(Strings.Startup_Preferences_Detail, EnumDisplayConverter.ToText(context.UserPrefs.Theme)));
    }

    private StepResult LoadProfiles(StartupContext context)
    {
        var profiles = _serverPrefs.LoadPreferences().ToList();
        context.Profile = SelectProfile(profiles, context.UserPrefs.LastActiveProfile);
        context.ServerExe = ExpandOrNull(OverrideOr(context.Profile?.ServerExePath, context.UserPrefs.ServerExePath));
        var saveFolder = ExpandOrNull(OverrideOr(context.Profile?.SaveDataFolderPath, context.UserPrefs.SaveDataFolderPath));
        context.SaveFolder = saveFolder is null ? null : new DirectoryInfo(saveFolder);
        context.World = string.IsNullOrWhiteSpace(context.Profile?.WorldName) ? null : context.Profile.WorldName;

        if (profiles.Count == 0) return new StepResult(StartupStepState.Done, Strings.Startup_Profiles_None);
        var count = string.Format(profiles.Count == 1 ? Strings.Startup_Profiles_One : Strings.Startup_Profiles_Other, profiles.Count);
        return new StepResult(StartupStepState.Done, string.Format(Strings.Startup_Profiles_Detail, count, context.Profile!.ProfileName));
    }

    // The profile the first window opens: the one named on the command line, else the startup selection's first.
    private ServerPreferences? SelectProfile(IReadOnlyList<ServerPreferences> profiles, string? lastActive)
    {
        var named = _startupArgs.ServerProfileName;
        var match = string.IsNullOrWhiteSpace(named) ? null : profiles.FirstOrDefault(p => p.ProfileName == named);
        return match ?? StartupProfileSelector.Select(profiles, lastActive).Windows.FirstOrDefault()?.Profile;
    }

    private StepResult FindServer(StartupContext context)
    {
        if (context.ServerExe is null || !File.Exists(context.ServerExe))
            return new StepResult(StartupStepState.Warning, Strings.Startup_Server_Missing);

        var build = _steamCmd.GetInstalledBuildId(Path.GetDirectoryName(context.ServerExe)!);
        return new StepResult(StartupStepState.Done,
            build is null ? Strings.Startup_Server_Found : string.Format(Strings.Startup_Server_FoundBuild, build));
    }

    private static StepResult CheckPlugin(StartupContext context) => ServerPluginInstaller.Check(context.ServerExe) switch
    {
        ServerPluginState.Installed => new StepResult(StartupStepState.Done, Strings.Startup_Plugin_Installed),
        ServerPluginState.Outdated => new StepResult(StartupStepState.Warning, Strings.Startup_Plugin_Outdated),
        ServerPluginState.NotInstalled => new StepResult(StartupStepState.Skipped, Strings.Startup_Plugin_NotInstalled),
        ServerPluginState.NoBepInEx => new StepResult(StartupStepState.Skipped, Strings.Startup_Plugin_NoBepInEx),
        _ => new StepResult(StartupStepState.Skipped, Strings.Startup_Plugin_NoServer),
    };

    private static StepResult ReadWorld(StartupContext context)
    {
        if (context.World is null || context.SaveFolder is null)
            return new StepResult(StartupStepState.Skipped, Strings.Startup_World_None);

        var folder = context.SaveFolder.GetLocalWorldFolder(context.World);
        if (!folder.Exists)
            return new StepResult(StartupStepState.Skipped, string.Format(Strings.Startup_World_NotCreated, context.World));

        var seed = WorldMetadataReader.ReadFromWorldFolder(folder).SeedName;
        return new StepResult(StartupStepState.Done, string.IsNullOrWhiteSpace(seed)
            ? context.World
            : string.Format(Strings.Startup_World_Seed, context.World, seed));
    }

    private StepResult CountBackups(StartupContext context)
    {
        if (context.World is null || context.SaveFolder is null)
            return new StepResult(StartupStepState.Skipped, Strings.Startup_World_None);

        var count = _backups.GetBackups(context.SaveFolder, context.World).Count;
        return new StepResult(StartupStepState.Done, count switch
        {
            0 => Strings.Startup_Backups_None,
            1 => string.Format(Strings.Startup_Backups_One, count),
            _ => string.Format(Strings.Startup_Backups_Other, count),
        });
    }

    private async Task<StepResult> LoadPlayersAsync()
    {
        await _playerData.LoadAsync();
        var count = _playerData.Data.Count();
        return new StepResult(StartupStepState.Done,
            string.Format(count == 1 ? Strings.Startup_Players_One : Strings.Startup_Players_Other, count));
    }

    private async Task<StepResult> CheckForUpdatesAsync(StartupContext context)
    {
        if (!context.UserPrefs.CheckForUpdates)
            return new StepResult(StartupStepState.Skipped, Strings.Startup_Updates_Off);

        await _updateProvider.CheckForUpdatesAsync(isManualCheck: false);
        return _updateProvider.LastResult switch
        {
            null => new StepResult(StartupStepState.Skipped, Strings.Startup_Updates_NotChecked),
            { IsSuccessful: false } => new StepResult(StartupStepState.Warning, Strings.Update_StatusFailed),
            { LatestVersion: { } latest } when AssemblyHelper.CompareVersion(latest) > 0
                => new StepResult(StartupStepState.Warning, string.Format(Strings.Update_StatusAvailable, latest)),
            { LatestVersion: var latest } => new StepResult(StartupStepState.Done, string.Format(Strings.Update_StatusUpToDate, latest)),
        };
    }

    private async Task<StepResult> RunSafelyAsync(string what, Func<Task<StepResult>> task)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _logger.Debug("Starting startup task: {task}", what);
        try
        {
            var result = await task();
            _logger.Debug("Finished startup task: {task} ({ms}ms)", what, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            // A startup task failing must never block the app from opening.
            _logger.Error("Startup task failed: {task} - {message}", what, ex.Message);
            return new StepResult(StartupStepState.Warning, string.Format(Strings.Startup_StepFailed, ex.Message));
        }
    }

    private static string? OverrideOr(string? profileValue, string appValue)
        => !string.IsNullOrWhiteSpace(profileValue) ? profileValue : string.IsNullOrWhiteSpace(appValue) ? null : appValue;

    private static string? ExpandOrNull(string? path) => path is null ? null : Environment.ExpandEnvironmentVariables(path);
}
