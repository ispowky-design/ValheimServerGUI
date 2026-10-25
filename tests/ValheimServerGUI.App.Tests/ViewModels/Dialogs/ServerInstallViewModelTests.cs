using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.App.ViewModels.Dialogs;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Logging;
using Xunit;

namespace ValheimServerGUI.App.Tests.ViewModels.Dialogs;

/// <summary>
/// Install / Update Server: the suggested folder, install + remembered folder, the build comparison after a check,
/// Use This Server, the running-server guard, failure and cancel reporting.
/// </summary>
public sealed class ServerInstallViewModelTests : IDisposable
{
    private static readonly IApplicationLogger Log =
        new ServiceCollection().AddValheimCore().BuildServiceProvider().GetRequiredService<IApplicationLogger>();

    private readonly string _root = Path.Join(Path.GetTempPath(), "vsg_install_vm_" + Guid.NewGuid().ToString("N"));
    private readonly FakeSteamCmdService _steamCmd;
    private readonly FakeUserPreferencesProvider _prefs = new();
    private readonly RecordingShellLauncher _shell = new();
    private bool _inUse;
    private string? _error;

    public ServerInstallViewModelTests() => _steamCmd = new FakeSteamCmdService(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private ServerInstallViewModel Build() => new(_steamCmd, _prefs, _shell, Log, _ => _inUse)
    {
        ErrorReported = msg => _error = msg,
    };

    [AvaloniaFact]
    public void Starts_on_the_suggested_folder_with_nothing_installed()
    {
        var vm = Build();

        Assert.Equal(_steamCmd.DefaultInstallFolder, vm.InstallFolder);
        Assert.Equal(Strings.SteamCmd_NotInstalled, vm.InstalledBuildText);
        Assert.Equal(Strings.SteamCmd_Unknown, vm.LatestBuildText);
        Assert.False(vm.UseServerCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Install_remembers_the_folder_and_shows_the_build()
    {
        var vm = Build();
        vm.Validate = true;

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal((Path.GetFullPath(_steamCmd.DefaultInstallFolder), true), Assert.Single(_steamCmd.Installs));
        Assert.Equal(Path.GetFullPath(_steamCmd.DefaultInstallFolder), _prefs.LoadPreferences().ServerInstallFolder);
        Assert.Equal("100", vm.InstalledBuildText);
        Assert.Equal(Strings.SteamCmd_Status_Installed, vm.StatusMessage);
        Assert.Contains(vm.Log, l => l.Contains("progress: 50.00"));
        Assert.False(vm.IsBusy);
        Assert.True(vm.UseServerCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Check_for_update_compares_installed_and_latest()
    {
        var vm = Build();
        await vm.InstallCommand.ExecuteAsync(null);

        _steamCmd.LatestBuild = "101";
        await vm.CheckForUpdateCommand.ExecuteAsync(null);
        Assert.Equal(string.Format(Strings.SteamCmd_UpdateAvailable, "100"), vm.InstalledBuildText);
        Assert.Equal("101", vm.LatestBuildText);

        _steamCmd.InstallBuild = "101";
        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal(string.Format(Strings.SteamCmd_UpToDate, "101"), vm.InstalledBuildText);
    }

    [AvaloniaFact]
    public async Task Use_this_server_sets_the_app_wide_server_path()
    {
        var vm = Build();
        await vm.InstallCommand.ExecuteAsync(null);

        vm.UseServerCommand.Execute(null);

        Assert.Equal(_steamCmd.GetServerExePath(Path.GetFullPath(_steamCmd.DefaultInstallFolder)), _prefs.LoadPreferences().ServerExePath);
        Assert.Equal(Strings.SteamCmd_Status_UsingServer, vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Refuses_to_update_a_folder_a_running_server_uses()
    {
        _inUse = true;
        var vm = Build();

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Empty(_steamCmd.Installs);
        Assert.Equal(Strings.SteamCmd_ServerRunning, _error);
    }

    [AvaloniaFact]
    public async Task A_failed_run_is_reported_and_logged()
    {
        _steamCmd.Failure = new InvalidOperationException("boom");
        var vm = Build();

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal("boom", _error);
        Assert.Contains("boom", vm.Log);
        Assert.Null(vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Cancel_stops_a_running_install()
    {
        _steamCmd.WaitForCancel = true;
        var vm = Build();

        var run = vm.InstallCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.True(vm.CancelCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await run;

        Assert.Equal(Strings.SteamCmd_Status_Cancelled, vm.StatusMessage);
        Assert.Null(_error);
        Assert.False(vm.IsBusy);
    }
}
