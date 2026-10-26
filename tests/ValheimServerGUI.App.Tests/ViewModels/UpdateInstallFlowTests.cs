using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerGUI.App.Services;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.App.Tests.Services;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Logging;
using ValheimServerGUI.Tools.Models;
using Xunit;

namespace ValheimServerGUI.App.Tests.ViewModels;

/// <summary>
/// A newer release this copy can install downloads in the background and installs on one click, restarting the app;
/// it never installs while a server runs, since closing the app stops its servers.
/// </summary>
public sealed class UpdateInstallFlowTests : IDisposable
{
    private static readonly IServiceProvider Core =
        new ServiceCollection().AddValheimCore().BuildServiceProvider();

    private const string ReleasePage = "https://github.com/o/r/releases/tag/v9.9.9";
    private const string FileName = "ValheimServerGUI-9.9.9-win-x64-portable.exe";

    private readonly string _dir;
    private readonly string _exe;
    private readonly string _saveDir;
    private readonly FakeSoftwareUpdateProvider _updates = new();
    private readonly FakeAppUpdater _updater = new();
    private readonly RecordingSystemShell _shell = new();
    private readonly List<string> _asked = new();
    private readonly List<(string Title, string Body)> _shown = new();
    private readonly List<string> _installed = new();
    private bool _answer = true;

    public UpdateInstallFlowTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsg-update-" + Guid.NewGuid().ToString("N"));
        _saveDir = Path.Combine(_dir, "save");
        Directory.CreateDirectory(Path.Combine(_saveDir, "worlds"));
        _exe = Path.Combine(_dir, "valheim_server.x86_64");
        File.WriteAllText(_exe, "");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private MainWindowViewModel Build()
    {
        var shell = new ShellLauncher(_shell, TestLog.Silent, isWindows: false);
        var baselines = new InMemoryBaselineStore();
        var accessLists = Core.GetRequiredService<IPlayerAccessListService>();
        var import = new PlayerListImportService(accessLists, baselines);
        var manager = new ServerManager(
            () => new ValheimServer(new FakeProcessProvider(), Core.GetRequiredService<IPlayerDataRepository>(),
                Core.GetRequiredService<IApplicationLogger>(), Core.GetRequiredService<IValheimPathResolver>(),
                accessLists, import, baselines),
            Core.GetRequiredService<Serilog.ILogger>());

        var vm = new MainWindowViewModel(
            manager, new FakeUserPreferencesProvider(), new FakeServerPreferencesProvider(new[] { new ServerPreferences { ProfileName = "A" } }),
            Core.GetRequiredService<IWorldPreferencesProvider>(), new FakeSteamCloudWorldProvider(),
            Core.GetRequiredService<IIpAddressProvider>(), Core.GetRequiredService<IPlayerDataRepository>(),
            Core.GetRequiredService<IApplicationLogger>(), _updates, shell,
            Core.GetRequiredService<IValheimPathResolver>(), import, appUpdater: _updater);
        vm.LoadProfile(new ServerPreferences { ProfileName = "A" });
        vm.UpdateResultPrompt = body => { _asked.Add(body); return Task.FromResult(_answer); };
        vm.MessagePrompt = (title, body) => { _shown.Add((title, body)); return Task.CompletedTask; };
        vm.UpdateDownloadPrompt = async dialog => await dialog.RunAsync() ? dialog.DownloadedFile : null;
        vm.InstallUpdateAndExit = _installed.Add;
        return vm;
    }

    private static SoftwareUpdateEventArgs Release(bool manual = false)
        => new("9.9.9", ReleasePage, manual, new[] { new ReleaseAsset { Name = FileName, DownloadUrl = "https://x/" + FileName } });

    // An automatic check finds the release; the "ready to install" notice it raises is declined, so a test starts
    // from the status-bar offer.
    private void Found()
    {
        _answer = false;
        _updates.RaiseFinished(Release());
        _asked.Clear();
        _answer = true;
    }

    private void StartServer(MainWindowViewModel vm)
        => vm.Server!.Start(new ValheimServerOptions
        {
            Name = "Srv", WorldName = "Worldxx", Password = "hunter2", Port = 2456, SaveInterval = 30,
            Backups = 1, BackupShort = 60, BackupLong = 120, ServerExePath = _exe, SaveDataFolderPath = _saveDir, LogToFile = false,
        });

    [AvaloniaFact]
    public void A_found_update_downloads_in_the_background_and_shows_ready()
    {
        var vm = Build();

        Found();

        Assert.Equal(new[] { "9.9.9" }, _updater.Downloads);
        Assert.Equal(string.Format(Strings.Update_StatusReady, "9.9.9"), vm.UpdateStatusText);
        Assert.Equal(UpdateCheckStatus.Available, vm.UpdateStatus);
        Assert.True(vm.UpdateIsLink);
    }

    [AvaloniaFact]
    public async Task Clicking_the_ready_update_asks_then_installs_and_exits()
    {
        var vm = Build();
        Found();

        await vm.UpdateLinkCommand.ExecuteAsync(null);

        Assert.Empty(_shown);
        Assert.Equal(new[] { string.Format(Strings.Update_InstallReadyPrompt, "9.9.9") }, _asked);
        Assert.Equal(new[] { "/downloads/" + FileName }, _installed);
        Assert.Empty(_shell.Started);
    }

    [AvaloniaFact]
    public async Task Declining_installs_nothing()
    {
        var vm = Build();
        Found();
        _answer = false;

        await vm.UpdateLinkCommand.ExecuteAsync(null);

        Assert.Empty(_installed);
    }

    [AvaloniaFact]
    public async Task Never_installs_while_a_server_runs()
    {
        var vm = Build();
        Found();
        StartServer(vm);
        Assert.NotEqual(ServerStatus.Stopped, vm.ServerStatus);

        await vm.UpdateLinkCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (Strings.Prompt_CheckForUpdates_Title, Strings.Update_StopServersFirst) }, _shown);
        Assert.Empty(_asked);
        Assert.Empty(_installed);
    }

    [AvaloniaFact]
    public async Task A_server_started_during_the_download_stops_the_install()
    {
        var vm = Build();
        Found();
        vm.UpdateDownloadPrompt = async dialog =>
        {
            StartServer(vm);
            return await dialog.RunAsync() ? dialog.DownloadedFile : null;
        };

        await vm.UpdateLinkCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (Strings.Prompt_CheckForUpdates_Title, Strings.Update_ServersStartedDuringDownload) }, _shown);
        Assert.Empty(_installed);
    }

    [AvaloniaFact]
    public async Task A_failed_download_installs_nothing_and_the_dialog_says_why()
    {
        _updater.DownloadError = new Exception("no network");
        var vm = Build();
        Found();
        Assert.Equal(string.Format(Strings.Update_StatusAvailable, "9.9.9"), vm.UpdateStatusText); // never "ready"

        string? message = null;
        vm.UpdateDownloadPrompt = async dialog =>
        {
            var ok = await dialog.RunAsync();
            message = dialog.Message;
            return ok ? dialog.DownloadedFile : null;
        };
        await vm.UpdateLinkCommand.ExecuteAsync(null);

        Assert.Equal(string.Format(Strings.AsyncOperation_Failure, Strings.Update_Download_Failed, "no network"), message);
        Assert.Empty(_installed);
    }

    [AvaloniaFact]
    public async Task An_install_that_cannot_start_is_reported_and_the_app_stays()
    {
        var vm = Build();
        Found();
        vm.InstallUpdateAndExit = _ => throw new IOException("file in use");

        await vm.UpdateLinkCommand.ExecuteAsync(null);

        Assert.Equal(new[] { (Strings.Prompt_CheckForUpdates_Title, string.Format(Strings.Update_InstallFailed, "file in use")) }, _shown);
    }

    [AvaloniaFact]
    public async Task A_manual_check_offers_to_download_and_install()
    {
        var vm = Build();

        _updates.RaiseFinished(Release(manual: true));
        await Task.Yield();

        Assert.Equal(string.Format(Strings.Update_InstallPrompt, Strings.Update_ManualAvailable), _asked.First());
        Assert.Equal(new[] { "/downloads/" + FileName }, _installed);
        Assert.Empty(_shell.Started);
    }

    [AvaloniaFact]
    public void A_copy_that_cannot_update_itself_still_opens_the_release_page()
    {
        _updater.InstallKind = AppInstallKind.Unsupported;
        var vm = Build();
        Found();

        vm.UpdateLinkCommand.Execute(null);

        Assert.Empty(_updater.Downloads);
        Assert.Equal(string.Format(Strings.Update_StatusAvailable, "9.9.9"), vm.UpdateStatusText);
        Assert.Equal(new[] { ReleasePage }, _shell.Started.SelectMany(s => s.Args));
    }

    // ---- telling the user ----

    [AvaloniaFact]
    public void A_ready_update_is_announced_once_and_yes_installs_it()
    {
        Build();

        _updates.RaiseFinished(Release());

        Assert.Equal(new[] { string.Format(Strings.Update_InstallReadyPrompt, "9.9.9") }, _asked);
        Assert.Equal(new[] { "/downloads/" + FileName }, _installed);
    }

    [AvaloniaFact]
    public void A_later_check_or_another_window_does_not_announce_it_again()
    {
        Build();
        Found();
        Build();

        _updates.RaiseFinished(Release());

        Assert.Empty(_asked);
        Assert.Empty(_installed);
    }

    [AvaloniaFact]
    public async Task A_manual_check_asks_once_not_twice()
    {
        Build();

        _updates.RaiseFinished(Release(manual: true));
        await Task.Yield();

        Assert.Single(_asked);
    }

    [AvaloniaFact]
    public void A_failed_periodic_check_keeps_the_ready_update()
    {
        var vm = Build();
        Found();

        _updates.RaiseFinished(new SoftwareUpdateEventArgs(new Exception("offline"), isManualCheck: false));

        Assert.Equal(string.Format(Strings.Update_StatusReady, "9.9.9"), vm.UpdateStatusText);
        Assert.True(vm.UpdateIsLink);
    }

    [AvaloniaFact]
    public async Task A_periodic_check_starting_keeps_showing_the_ready_update()
    {
        var vm = Build();
        Found();

        await _updates.CheckForUpdatesAsync(isManualCheck: false);

        Assert.Equal(string.Format(Strings.Update_StatusReady, "9.9.9"), vm.UpdateStatusText);
    }
}
