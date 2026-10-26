using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerGUI.App.Converters;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Logging;
using Xunit;

namespace ValheimServerGUI.App.Tests.ViewModels;

/// <summary>
/// The Backups tab over a real temp save folder: listing for the selected world, the no-world state, Back Up Now,
/// the restore gate while the world is in use, confirm-or-cancel on restore/delete, and delete limited to VSG zips.
/// </summary>
public sealed class BackupsViewModelTests : IDisposable
{
    private static readonly IServiceProvider Core =
        new ServiceCollection().AddValheimCore().BuildServiceProvider();

    private readonly DirectoryInfo _saveFolder;
    private readonly string _worldsLocal;
    private readonly RecordingShellLauncher _shell = new();
    private string? _world = "Viking";
    private bool _inUse;
    private bool _confirmAnswer = true;
    private int _confirmCount;
    private string? _error;

    public BackupsViewModelTests()
    {
        _saveFolder = new DirectoryInfo(Path.Join(Path.GetTempPath(), "vsg_backups_vm_" + Guid.NewGuid().ToString("N")));
        _worldsLocal = Path.Join(_saveFolder.FullName, "worlds_local");
        Directory.CreateDirectory(_worldsLocal);
    }

    public void Dispose()
    {
        if (_saveFolder.Exists) _saveFolder.Delete(true);
    }

    private BackupsViewModel Build()
    {
        var vm = new BackupsViewModel(new WorldBackupService(), _shell, Core.GetRequiredService<IApplicationLogger>(),
            () => _saveFolder, () => _world, () => _inUse)
        {
            ConfirmPrompt = (_, _) =>
            {
                _confirmCount++;
                return Task.FromResult(_confirmAnswer);
            },
            ErrorReported = msg => _error = msg,
        };
        vm.SetActive(true);
        return vm;
    }

    private void WriteWorld(string name, string content)
    {
        var dir = Directory.CreateDirectory(Path.Join(_worldsLocal, name));
        File.WriteAllText(Path.Join(dir.FullName, "_main.1.fwl2"), content);
    }

    private string ReadWorld() => File.ReadAllText(Path.Join(_worldsLocal, "Viking", "_main.1.fwl2"));

    [AvaloniaFact]
    public void No_world_selected_shows_the_hint_and_disables_every_action()
    {
        _world = null;
        var vm = Build();

        Assert.Null(vm.CurrentWorld);
        Assert.Equal(Strings.Backups_World_None, vm.WorldText);
        Assert.Equal(Strings.Backups_NoWorldText, vm.EmptyText);
        Assert.False(vm.BackUpNowCommand.CanExecute(null));
        Assert.False(vm.OpenFolderCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void Lists_both_kinds_newest_first_with_display_text()
    {
        WriteWorld("Viking", "now");
        WriteWorld("Viking_backup_auto-20261020120000", "auto");
        WriteWorld("Viking_backup_auto-20261021120000", "auto2");

        var vm = Build();

        Assert.Equal("Viking", vm.WorldText);
        Assert.Null(vm.EmptyText);
        Assert.Equal(new[] { 21, 20 }, vm.Backups.Select(b => b.CreatedAt.Day));
        Assert.All(vm.Backups, b => Assert.Equal(EnumDisplayConverter.ToText(WorldBackupKind.Automatic), b.KindText));
    }

    [AvaloniaFact]
    public async Task Back_up_now_adds_a_manual_backup_and_reports_it()
    {
        WriteWorld("Viking", "now");
        var vm = Build();
        Assert.True(vm.BackUpNowCommand.CanExecute(null));

        await vm.BackUpNowCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.Backups);
        Assert.True(row.IsVsgBackup);
        Assert.Equal(string.Format(Strings.Backups_Status_Created, Path.GetFileName(row.Backup.Path)), vm.StatusMessage);
        Assert.False(vm.IsBusy);
    }

    // A world name with no save yet (a new world never started) has nothing to back up.
    [AvaloniaFact]
    public void Back_up_now_is_disabled_until_the_world_has_a_save()
    {
        var vm = Build();

        Assert.False(vm.WorldExists);
        Assert.Equal(Strings.Backups_EmptyText, vm.EmptyText);
        Assert.False(vm.BackUpNowCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Restore_replaces_the_world_after_confirming()
    {
        WriteWorld("Viking", "old");
        var vm = Build();
        await vm.BackUpNowCommand.ExecuteAsync(null);
        File.WriteAllText(Path.Join(_worldsLocal, "Viking", "_main.1.fwl2"), "new");
        vm.SelectedBackup = vm.Backups.Single();

        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal(1, _confirmCount);
        Assert.Equal("old", ReadWorld());
        // The pre-restore state was kept as a second backup.
        Assert.Equal(2, vm.Backups.Count);
    }

    [AvaloniaFact]
    public async Task Restore_cancelled_at_the_prompt_changes_nothing()
    {
        WriteWorld("Viking", "old");
        var vm = Build();
        await vm.BackUpNowCommand.ExecuteAsync(null);
        File.WriteAllText(Path.Join(_worldsLocal, "Viking", "_main.1.fwl2"), "new");
        vm.SelectedBackup = vm.Backups.Single();
        _confirmAnswer = false;

        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal("new", ReadWorld());
        Assert.Single(vm.Backups);
    }

    [AvaloniaFact]
    public async Task Restore_is_blocked_while_a_server_runs_the_world()
    {
        WriteWorld("Viking", "old");
        var vm = Build();
        await vm.BackUpNowCommand.ExecuteAsync(null);
        vm.SelectedBackup = vm.Backups.Single();

        _inUse = true;
        vm.RefreshWorldInUse();

        Assert.True(vm.IsWorldInUse);
        Assert.False(vm.RestoreCommand.CanExecute(null));
        // Backing up a running world stays allowed.
        Assert.True(vm.BackUpNowCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Delete_is_offered_for_manual_backups_only()
    {
        WriteWorld("Viking", "old");
        WriteWorld("Viking_backup_auto-20261020120000", "auto");
        var vm = Build();
        await vm.BackUpNowCommand.ExecuteAsync(null);

        vm.SelectedBackup = vm.Backups.Single(b => !b.IsVsgBackup);
        Assert.False(vm.DeleteCommand.CanExecute(null));

        vm.SelectedBackup = vm.Backups.Single(b => b.IsVsgBackup);
        await vm.DeleteCommand.ExecuteAsync(null);

        Assert.Equal(1, _confirmCount);
        Assert.DoesNotContain(vm.Backups, b => b.IsVsgBackup);
    }

    [AvaloniaFact]
    public void Open_folder_creates_and_opens_the_world_backup_folder()
    {
        var vm = Build();

        vm.OpenFolderCommand.Execute(null);

        var expected = Path.Join(_saveFolder.FullName, WorldBackupService.BackupFolderName, "Viking");
        Assert.Equal(new[] { expected }, _shell.OpenedDirectories);
        Assert.True(Directory.Exists(expected));
    }

    [AvaloniaFact]
    public void A_hidden_tab_does_not_read_the_disk_on_selection_changes()
    {
        WriteWorld("Viking_backup_auto-20261020120000", "auto");
        var vm = Build();
        vm.SetActive(false);
        _world = "Other";

        vm.OnSelectionChanged();

        Assert.Equal("Viking", vm.CurrentWorld);
        vm.SetActive(true);
        Assert.Equal("Other", vm.CurrentWorld);
        Assert.Empty(vm.Backups);
    }

    [Fact]
    public void Sizes_read_in_kilobytes_then_megabytes()
    {
        Assert.Equal(string.Format(Strings.Backups_Size_KB, 1), BackupRowViewModel.FormatSize(10));
        Assert.Equal(string.Format(Strings.Backups_Size_KB, 2), BackupRowViewModel.FormatSize(1025));
        Assert.Equal(string.Format(Strings.Backups_Size_MB, 1.5), BackupRowViewModel.FormatSize(1024 * 1024 * 3 / 2));
    }
}
