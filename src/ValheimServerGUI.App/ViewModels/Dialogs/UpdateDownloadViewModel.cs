using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;

namespace ValheimServerGUI.App.ViewModels.Dialogs;

/// <summary>
/// The update download dialog: shows the progress of <see cref="IAppUpdater.DownloadAsync"/> (joining a background
/// download already under way), then either hands back the downloaded file or reports why it failed.
/// </summary>
public partial class UpdateDownloadViewModel : ViewModelBase
{
    private readonly IAppUpdater _updater;
    private readonly SoftwareUpdateEventArgs _update;

    public UpdateDownloadViewModel(IAppUpdater updater, SoftwareUpdateEventArgs update)
    {
        _updater = updater;
        _update = update;
        _message = string.Format(Strings.Update_Download_Running, update.LatestVersion);
        _updater.DownloadProgressChanged += OnProgress;
    }

    /// <summary>Download progress, 0 to 100.</summary>
    [ObservableProperty] private double _progress;

    [ObservableProperty] private string _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private bool _failed;

    public bool IsRunning => !Failed;

    /// <summary>The downloaded file once <see cref="RunAsync"/> succeeded; null otherwise.</summary>
    public string? DownloadedFile { get; private set; }

    /// <summary>Downloads the update. Returns true when the file is ready to install.</summary>
    public async Task<bool> RunAsync()
    {
        try
        {
            DownloadedFile = await _updater.DownloadAsync(_update);
            Progress = 100;
            return true;
        }
        catch (Exception ex)
        {
            Failed = true;
            Message = string.Format(Strings.AsyncOperation_Failure, Strings.Update_Download_Failed, ex.GetPrimaryException().Message);
            return false;
        }
    }

    private void OnProgress(object? sender, double fraction) => RunOnUi(() => Progress = fraction * 100);

    protected override void DisposeCore() => _updater.DownloadProgressChanged -= OnProgress;
}
