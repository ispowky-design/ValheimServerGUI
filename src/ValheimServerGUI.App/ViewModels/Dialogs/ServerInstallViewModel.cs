using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.App.ViewModels.Dialogs;

/// <summary>
/// Install / Update Server dialog: installs the dedicated server with SteamCMD into a chosen folder, compares the
/// installed build with Steam's latest on demand, and can point every profile at the installed server. Refuses to
/// update a folder a running server uses. SteamCMD's output streams into the dialog's log.
/// </summary>
public partial class ServerInstallViewModel : ViewModelBase
{
    private const int MaxLogLines = 2000;

    private readonly ISteamCmdService _steamCmd;
    private readonly IUserPreferencesProvider _userPrefs;
    private readonly IShellLauncher _shell;
    private readonly IApplicationLogger _logger;
    private readonly Func<string, bool> _isFolderInUse;
    private CancellationTokenSource? _cancel;
    private string? _latestBuild;

    public ServerInstallViewModel(
        ISteamCmdService steamCmd,
        IUserPreferencesProvider userPrefs,
        IShellLauncher shell,
        IApplicationLogger logger,
        Func<string, bool> isFolderInUse)
    {
        _steamCmd = steamCmd;
        _userPrefs = userPrefs;
        _shell = shell;
        _logger = logger;
        _isFolderInUse = isFolderInUse;

        var saved = _userPrefs.LoadPreferences().ServerInstallFolder;
        _installFolder = string.IsNullOrWhiteSpace(saved) ? _steamCmd.DefaultInstallFolder : saved;
        RefreshBuilds();
    }

    /// <summary>Surfaces a failed run. Wired by the window.</summary>
    public Action<string>? ErrorReported { get; set; }

    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(UseServerCommand), nameof(OpenFolderCommand))]
    private string? _installFolder;

    [ObservableProperty] private bool _validate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CheckForUpdateCommand), nameof(UseServerCommand), nameof(CancelCommand))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressIndeterminate;
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private string _installedBuildText = Strings.SteamCmd_NotInstalled;
    [ObservableProperty] private string _latestBuildText = Strings.SteamCmd_Unknown;

    /// <summary>The build installed in the chosen folder, or null.</summary>
    public string? InstalledBuild { get; private set; }

    partial void OnInstallFolderChanged(string? value) => RefreshBuilds();

    private void RefreshBuilds()
    {
        try
        {
            InstalledBuild = string.IsNullOrWhiteSpace(InstallFolder) ? null : _steamCmd.GetInstalledBuildId(InstallFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            InstalledBuild = null;
        }

        InstalledBuildText = InstalledBuild is null ? Strings.SteamCmd_NotInstalled
            : _latestBuild is null ? InstalledBuild
            : string.Format(InstalledBuild == _latestBuild ? Strings.SteamCmd_UpToDate : Strings.SteamCmd_UpdateAvailable, InstalledBuild);
        LatestBuildText = _latestBuild ?? Strings.SteamCmd_Unknown;
        UseServerCommand.NotifyCanExecuteChanged();
    }

    private bool HasFolder => !string.IsNullOrWhiteSpace(InstallFolder);

    private bool CanInstall => HasFolder && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task Install() => RunAsync(async (progress, token) =>
    {
        var folder = Path.GetFullPath(InstallFolder!);
        if (_isFolderInUse(folder)) throw new InvalidOperationException(Strings.SteamCmd_ServerRunning);

        var prefs = _userPrefs.LoadPreferences();
        prefs.ServerInstallFolder = folder;
        _userPrefs.SavePreferences(prefs);

        await _steamCmd.InstallOrUpdateServerAsync(folder, Validate, progress, token);
        return Strings.SteamCmd_Status_Installed;
    });

    private bool CanCheckForUpdate => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdate))]
    private Task CheckForUpdate() => RunAsync(async (progress, token) =>
    {
        _latestBuild = await _steamCmd.GetLatestBuildIdAsync(progress, token);
        return Strings.SteamCmd_Status_Checked;
    });

    private bool CanCancel => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cancel?.Cancel();

    private bool CanUseServer => HasFolder && !IsBusy && File.Exists(_steamCmd.GetServerExePath(InstallFolder!));

    /// <summary>Sets the app-wide server path (every profile without its own override) to the installed server.</summary>
    [RelayCommand(CanExecute = nameof(CanUseServer))]
    private void UseServer()
    {
        var prefs = _userPrefs.LoadPreferences();
        prefs.ServerExePath = _steamCmd.GetServerExePath(Path.GetFullPath(InstallFolder!));
        _userPrefs.SavePreferences(prefs);
        _logger.Information("Server path set to the SteamCMD install: {path}", prefs.ServerExePath);
        StatusMessage = Strings.SteamCmd_Status_UsingServer;
    }

    [RelayCommand(CanExecute = nameof(HasFolder))]
    private void OpenFolder()
    {
        var folder = InstallFolder!;
        if (Directory.Exists(folder)) _shell.OpenDirectory(folder);
    }

    private void Append(string line)
    {
        Log.Add(line);
        while (Log.Count > MaxLogLines) Log.RemoveAt(0);
    }

    /// <summary>Runs one SteamCMD job with live log + progress, then reports the outcome and re-reads the builds.</summary>
    private async Task RunAsync(Func<IProgress<SteamCmdProgress>, CancellationToken, Task<string>> job)
    {
        using var cancel = new CancellationTokenSource();
        _cancel = cancel;
        IsBusy = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        StatusMessage = Strings.SteamCmd_Status_Working;

        // SteamCMD reports from a background thread: marshal each line onto the UI thread, in order.
        var progress = new UiProgress(p => RunOnUi(() =>
        {
            Append(p.Line);
            if (p.Percent is { } pct)
            {
                IsProgressIndeterminate = false;
                Progress = pct;
            }
        }));

        try
        {
            StatusMessage = await job(progress, cancel.Token);
            Progress = 100;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = Strings.SteamCmd_Status_Cancelled;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException
                                      or HttpRequestException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            _logger.Error(e, "SteamCMD job failed: {message}", e.Message);
            StatusMessage = null;
            Append(e.Message);
            ErrorReported?.Invoke(e.Message);
        }
        finally
        {
            IsProgressIndeterminate = false;
            IsBusy = false;
            _cancel = null;
        }

        RefreshBuilds();
    }

    protected override void DisposeCore() => _cancel?.Cancel();

    // Unlike Progress<T>, never defers a report made on the UI thread, so the log is complete when the job ends.
    private sealed class UiProgress(Action<SteamCmdProgress> report) : IProgress<SteamCmdProgress>
    {
        public void Report(SteamCmdProgress value) => report(value);
    }
}
