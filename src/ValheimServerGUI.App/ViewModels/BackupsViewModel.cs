using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerGUI.App.Converters;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.App.ViewModels;

/// <summary>One row in the Backups grid.</summary>
public sealed class BackupRowViewModel
{
    public BackupRowViewModel(WorldBackup backup)
    {
        Backup = backup;
        CreatedText = backup.CreatedAt.ToString("g");
        KindText = EnumDisplayConverter.ToText(backup.Kind);
        SizeText = FormatSize(backup.SizeBytes);
    }

    public WorldBackup Backup { get; }
    public DateTime CreatedAt => Backup.CreatedAt;
    public long SizeBytes => Backup.SizeBytes;
    public string CreatedText { get; }
    public string KindText { get; }
    public string SizeText { get; }
    /// <summary>A zip VSG made (manual or scheduled), which the user may delete; the server rotates its own.</summary>
    public bool IsVsgBackup => Backup.Kind != WorldBackupKind.Automatic;

    internal static string FormatSize(long bytes)
    {
        const double mb = 1024 * 1024;
        return bytes >= mb
            ? string.Format(Strings.Backups_Size_MB, bytes / mb)
            : string.Format(Strings.Backups_Size_KB, Math.Max(1, (bytes + 1023) / 1024));
    }
}

/// <summary>
/// Backups tab: the selected world's backups (VSG zips and the server's own automatic backups), with Back Up Now,
/// Restore, Delete and Open Folder. The world and save folder come from the window's current selection; a restore
/// is refused while any server runs that world. File work runs off the UI thread.
/// </summary>
public partial class BackupsViewModel : ViewModelBase
{
    private readonly IWorldBackupService _service;
    private readonly IShellLauncher _shell;
    private readonly IApplicationLogger _logger;
    private readonly Func<DirectoryInfo?> _saveFolder;
    private readonly Func<string?> _worldName;
    private readonly Func<bool> _worldInUse;

    public BackupsViewModel(
        IWorldBackupService service,
        IShellLauncher shell,
        IApplicationLogger logger,
        Func<DirectoryInfo?> saveFolder,
        Func<string?> worldName,
        Func<bool> worldInUse)
    {
        _service = service;
        _shell = shell;
        _logger = logger;
        _saveFolder = saveFolder;
        _worldName = worldName;
        _worldInUse = worldInUse;

        Backups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EmptyText));
    }

    /// <summary>Asks a yes/no question (title, body); true = yes. Wired by the window; treated as "no" if unset.</summary>
    public Func<string, string, Task<bool>>? ConfirmPrompt { get; set; }

    /// <summary>Surfaces a failed operation. Wired by the window.</summary>
    public Action<string>? ErrorReported { get; set; }

    public ObservableCollection<BackupRowViewModel> Backups { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand), nameof(DeleteCommand))]
    private BackupRowViewModel? _selectedBackup;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText))]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand), nameof(RestoreCommand), nameof(DeleteCommand), nameof(OpenFolderCommand))]
    private string? _currentWorld;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand))]
    private bool _worldExists;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackUpNowCommand), nameof(RestoreCommand), nameof(DeleteCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _worldText = Strings.Backups_World_None;
    /// <summary>The overlay shown over an empty list: why it is empty. Null while there are rows.</summary>
    public string? EmptyText => Backups.Count > 0 ? null
        : CurrentWorld is null ? Strings.Backups_NoWorldText : Strings.Backups_EmptyText;

    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private bool _isWorldInUse;

    private bool _isActive;

    /// <summary>Called by the view when the tab becomes visible/hidden: the list is read from disk only while shown.</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active) Refresh();
    }

    /// <summary>The window's world or save folder changed; re-read now if the tab is showing.</summary>
    public void OnSelectionChanged()
    {
        if (_isActive) Refresh();
    }

    /// <summary>Re-reads the selected world and its backups from disk. Keeps the selection when it still exists.</summary>
    public void Refresh()
    {
        var world = _worldName();
        var folder = string.IsNullOrWhiteSpace(world) ? null : _saveFolder();
        var previous = SelectedBackup?.Backup.Path;

        CurrentWorld = folder is null ? null : world;
        WorldText = CurrentWorld ?? Strings.Backups_World_None;
        RefreshWorldInUse();

        Backups.Clear();
        SelectedBackup = null;
        if (folder is null || CurrentWorld is null)
        {
            WorldExists = false;
            return;
        }

        try
        {
            WorldExists = _service.WorldExists(folder, CurrentWorld);
            foreach (var backup in _service.GetBackups(folder, CurrentWorld))
            {
                var row = new BackupRowViewModel(backup);
                Backups.Add(row);
                if (backup.Path == previous) SelectedBackup = row;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Warning(e, "Could not list the backups of world {world}: {message}", CurrentWorld, e.Message);
            WorldExists = false;
        }
    }

    /// <summary>Re-checks whether a running server holds the world (called on any server status change).</summary>
    public void RefreshWorldInUse()
    {
        IsWorldInUse = CurrentWorld is not null && _worldInUse();
        RestoreCommand.NotifyCanExecuteChanged();
    }

    private bool CanBackUpNow => CurrentWorld is not null && WorldExists && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBackUpNow))]
    private Task BackUpNow() => RunAsync(folder =>
    {
        var backup = _service.CreateBackup(folder, CurrentWorld!);
        _logger.Information("Backed up world {world} to {path}", backup.WorldName, backup.Path);
        return string.Format(Strings.Backups_Status_Created, Path.GetFileName(backup.Path));
    });

    private bool CanRestore => SelectedBackup is not null && !IsBusy && !IsWorldInUse;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore()
    {
        var row = SelectedBackup!;

        // The server may have started since the list was drawn; re-check at the moment it matters.
        RefreshWorldInUse();
        if (IsWorldInUse)
        {
            ErrorReported?.Invoke(Strings.Backups_WorldInUse);
            return;
        }

        var message = string.Format(Strings.Prompt_RestoreBackup_Message, row.Backup.WorldName, row.CreatedAt);
        if (ConfirmPrompt is null || !await ConfirmPrompt(Strings.Prompt_RestoreBackup_Title, message)) return;

        await RunAsync(folder =>
        {
            var safety = _service.RestoreBackup(folder, row.Backup);
            _logger.Information("Restored world {world} from {path}; previous state saved to {safety}",
                row.Backup.WorldName, row.Backup.Path, safety?.Path ?? "(nothing to save)");
            return safety is null
                ? string.Format(Strings.Backups_Status_Restored, row.CreatedAt)
                : string.Format(Strings.Backups_Status_RestoredWithSafety, row.CreatedAt, Path.GetFileName(safety.Path));
        });
    }

    private bool CanDelete => SelectedBackup is { IsVsgBackup: true } && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task Delete()
    {
        var row = SelectedBackup!;
        var message = string.Format(Strings.Prompt_DeleteBackup_Message, row.CreatedAt);
        if (ConfirmPrompt is null || !await ConfirmPrompt(Strings.Prompt_DeleteBackup_Title, message)) return;

        await RunAsync(_ =>
        {
            _service.DeleteBackup(row.Backup);
            _logger.Information("Deleted backup {path}", row.Backup.Path);
            return Strings.Backups_Status_Deleted;
        });
    }

    private bool CanOpenFolder => CurrentWorld is not null;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        var folder = _saveFolder();
        if (folder is null || CurrentWorld is null) return;

        try
        {
            var backups = _service.GetBackupFolder(folder, CurrentWorld);
            backups.Create();
            _shell.OpenDirectory(backups.FullName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorReported?.Invoke(e.Message);
        }
    }

    [RelayCommand]
    private void RefreshList() => Refresh();

    /// <summary>Runs one file operation off the UI thread, then reports its outcome and re-reads the list.</summary>
    private async Task RunAsync(Func<DirectoryInfo, string> operation)
    {
        var folder = _saveFolder();
        if (folder is null) return;

        IsBusy = true;
        StatusMessage = Strings.Backups_Status_Working;
        try
        {
            StatusMessage = await Task.Run(() => operation(folder));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
        {
            _logger.Error(e, "Backup operation on world {world} failed: {message}", CurrentWorld, e.Message);
            StatusMessage = null;
            ErrorReported?.Invoke(e.Message);
        }
        finally
        {
            IsBusy = false;
        }

        Refresh();
    }
}
