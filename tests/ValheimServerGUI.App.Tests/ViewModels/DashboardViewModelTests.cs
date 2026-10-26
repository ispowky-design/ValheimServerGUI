using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Logging;
using Xunit;

namespace ValheimServerGUI.App.Tests.ViewModels;

public class DashboardViewModelTests
{
    private static readonly IServiceProvider Core = new ServiceCollection().AddValheimCore().BuildServiceProvider();
    private static readonly DateTimeOffset T0 = DateTimeOffset.Now;

    private static (DashboardViewModel vm, FakePlayerDataRepository repo) Build()
    {
        var repo = new FakePlayerDataRepository();
        var players = new PlayersViewModel(repo, new ServerFormViewModel(), new FakeUserPreferencesProvider());
        var backups = new BackupsViewModel(new WorldBackupService(), new RecordingShellLauncher(),
            Core.GetRequiredService<IApplicationLogger>(), () => (DirectoryInfo?)null, () => null, () => false);
        return (new DashboardViewModel(players, backups, new ServerResourceMonitor(processorCount: 2)), repo);
    }

    private static ProcessUsageSample Sample(double cpuSeconds, long memory, double atSeconds)
        => new(TimeSpan.FromSeconds(cpuSeconds), memory, T0.AddSeconds(atSeconds));

    [AvaloniaFact]
    public void Starts_with_no_readings()
    {
        var (vm, _) = Build();
        Assert.Equal(Strings.Common_NotAvailable, vm.CpuText);
        Assert.Equal(Strings.Common_NotAvailable, vm.MemoryText);
        Assert.Empty(vm.CpuHistory);
        Assert.Equal("0", vm.PlayersOnlineText);
        Assert.False(vm.HasOnlinePlayers);
        Assert.Equal(Strings.Backups_World_None, vm.BackupsCaption);
    }

    [AvaloniaFact]
    public void Samples_fill_the_readouts_and_history()
    {
        var (vm, _) = Build();
        vm.Sample(Sample(0, 512L * 1024 * 1024, 0));
        vm.Sample(Sample(1, 3L * 1024 * 1024 * 1024, 1));

        Assert.Equal(string.Format(Strings.Dashboard_Cpu_Value, 50d), vm.CpuText);
        Assert.Equal(string.Format(Strings.Dashboard_Memory_Gigabytes, 3d), vm.MemoryText);
        Assert.Equal(new[] { 0d, 50d }, vm.CpuHistory);
        Assert.Equal(2, vm.MemoryHistory.Count);
    }

    [AvaloniaFact]
    public void History_keeps_one_minute()
    {
        var (vm, _) = Build();
        for (var i = 0; i < DashboardViewModel.HistoryLength + 15; i++)
            vm.Sample(Sample(i, 1, i));
        Assert.Equal(DashboardViewModel.HistoryLength, vm.CpuHistory.Count);
    }

    [AvaloniaFact]
    public void Losing_the_process_clears_the_readouts()
    {
        var (vm, _) = Build();
        vm.Sample(Sample(0, 1, 0));
        vm.Sample(null);
        Assert.Equal(Strings.Common_NotAvailable, vm.CpuText);
        Assert.Empty(vm.MemoryHistory);
    }

    [AvaloniaFact]
    public void Formats_memory_in_megabytes_below_a_gigabyte()
    {
        Assert.Equal(string.Format(Strings.Dashboard_Memory_Megabytes, 256d), DashboardViewModel.FormatBytes(256L * 1024 * 1024));
    }

    [AvaloniaFact]
    public void Lists_connected_players_and_tracks_the_peak()
    {
        var (vm, repo) = Build();
        repo.PushUpdate(Player("1", "Odin", PlayerStatus.Online));
        repo.PushUpdate(Player("2", "Freya", PlayerStatus.Joining));
        repo.PushUpdate(Player("3", "Loki", PlayerStatus.Offline));
        vm.Refresh();

        Assert.Equal("2", vm.PlayersOnlineText);
        Assert.True(vm.HasOnlinePlayers);
        Assert.Equal(new[] { "Freya", "Odin" }, vm.OnlinePlayers.Select(p => p.DisplayName));
        Assert.Equal(string.Format(Strings.Dashboard_Players_Caption, 2, 3), vm.PlayersCaption);

        repo.PushUpdate(Player("1", "Odin", PlayerStatus.Offline));
        vm.Refresh();
        Assert.Equal("1", vm.PlayersOnlineText);
        Assert.Equal(string.Format(Strings.Dashboard_Players_Caption, 2, 3), vm.PlayersCaption);
    }

    [AvaloniaFact]
    public void Status_flags_follow_the_server()
    {
        var (vm, _) = Build();
        vm.Status = ServerStatus.Starting;
        Assert.True(vm.IsBusy);
        vm.Status = ServerStatus.Running;
        Assert.True(vm.IsRunning);
        vm.Status = ServerStatus.Stopped;
        Assert.True(vm.IsStopped);
    }

    [AvaloniaFact]
    public void Shortcuts_ask_the_window_to_navigate()
    {
        var (vm, _) = Build();
        DashboardTarget? asked = null;
        vm.NavigateRequested += t => asked = t;
        vm.NavigateCommand.Execute(DashboardTarget.WorldMap);
        Assert.Equal(DashboardTarget.WorldMap, asked);
    }

    private static PlayerInfo Player(string id, string name, PlayerStatus status) => new()
    {
        Platform = "Steam",
        PlatformRaw = "Steam",
        PlayerId = id,
        PlayerName = name,
        LastStatusCharacter = null,
        PlayerStatus = status,
        LastStatusChange = DateTimeOffset.Now,
    };
}
