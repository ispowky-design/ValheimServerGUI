using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ValheimServerGUI.Game;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.App.ViewModels;

/// <summary>Where a dashboard shortcut sends the window.</summary>
public enum DashboardTarget
{
    ServerControls,
    Players,
    Backups,
    WorldMap,
    Logs,
}

/// <summary>
/// Dashboard tab: the selected server at a glance. Samples the server process's CPU and memory once a second
/// while the tab is visible (keeping a minute of history for the trend lines), and summarises the players online
/// and the world's latest backup from the Players and Backups tabs. Status, uptime, connection details and the
/// live log come straight from the window's other view-models. Only the process this app started is read, so a
/// server running outside the app is never touched.
/// </summary>
public partial class DashboardViewModel : ViewModelBase
{
    /// <summary>Points kept for each trend line: one minute at one sample a second.</summary>
    public const int HistoryLength = 60;

    private const double Gigabyte = 1024d * 1024 * 1024;
    private const double Megabyte = 1024d * 1024;

    private readonly PlayersViewModel _players;
    private readonly BackupsViewModel _backups;
    private readonly ServerResourceMonitor _monitor;
    private readonly DispatcherTimer _timer;
    private readonly List<double> _cpu = new();
    private readonly List<double> _memory = new();
    private ValheimServer? _server;
    private int _peakPlayers;

    public DashboardViewModel(PlayersViewModel players, BackupsViewModel backups, ServerResourceMonitor? monitor = null)
    {
        _players = players;
        _backups = backups;
        _monitor = monitor ?? new ServerResourceMonitor();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();

        TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        CpuCaption = string.Format(Strings.Dashboard_Cpu_Caption, Environment.ProcessorCount);
        MemoryCaption = TotalMemoryBytes > 0
            ? string.Format(Strings.Dashboard_Memory_Caption, TotalMemoryBytes / Gigabyte)
            : string.Empty;
        _backups.Backups.CollectionChanged += (_, _) => RefreshBackup();
        RefreshPlayers();
        RefreshBackup();
    }

    /// <summary>Raised by a shortcut that opens another tab; the window selects it.</summary>
    public event Action<DashboardTarget>? NavigateRequested;

    /// <summary>The machine's memory, the scale of the memory trend line (0 when unknown).</summary>
    public long TotalMemoryBytes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsBusy), nameof(IsStopped))]
    private ServerStatus _status;

    public bool IsRunning => Status == ServerStatus.Running;
    public bool IsBusy => Status is ServerStatus.Starting or ServerStatus.Stopping;
    public bool IsStopped => Status == ServerStatus.Stopped;

    [ObservableProperty] private string _cpuText = Strings.Common_NotAvailable;
    [ObservableProperty] private string _cpuCaption;
    [ObservableProperty] private string _memoryText = Strings.Common_NotAvailable;
    [ObservableProperty] private string _memoryCaption;
    [ObservableProperty] private IReadOnlyList<double> _cpuHistory = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _memoryHistory = Array.Empty<double>();

    [ObservableProperty] private string _playersOnlineText = "0";
    [ObservableProperty] private string _playersCaption = string.Empty;
    [ObservableProperty] private string _lastBackupText = Strings.Common_NotAvailable;
    [ObservableProperty] private string _backupsCaption = string.Empty;

    /// <summary>Players connected or connecting to the server, by name.</summary>
    public ObservableCollection<PlayerRowViewModel> OnlinePlayers { get; } = new();

    [ObservableProperty] private bool _hasOnlinePlayers;

    /// <summary>Re-targets onto the selected profile's server (profile switch): its history starts over.</summary>
    public void SetServer(ValheimServer next)
    {
        if (ReferenceEquals(next, _server)) return;
        _server = next;
        Status = next.Status;
        ResetUsage();
        _peakPlayers = 0;
        Refresh();
    }

    /// <summary>Called by the view when the tab becomes visible/hidden: the process is sampled only while shown.</summary>
    public void SetActive(bool active)
    {
        if (active)
        {
            _backups.Refresh();
            Refresh();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
        }
    }

    partial void OnStatusChanged(ServerStatus value)
    {
        if (value == ServerStatus.Stopped)
        {
            ResetUsage();
            _peakPlayers = 0;
        }
    }

    [RelayCommand]
    private void Navigate(DashboardTarget target) => NavigateRequested?.Invoke(target);

    /// <summary>One tick: sample the process, then re-read players and backups.</summary>
    internal void Refresh()
    {
        Sample(_server?.SampleProcessUsage());
        RefreshPlayers();
        RefreshBackup();
    }

    /// <summary>Applies one process sample (null when the server process isn't running).</summary>
    internal void Sample(ProcessUsageSample? sample)
    {
        if (_monitor.Next(sample) is not { } usage)
        {
            ResetUsage();
            return;
        }

        Push(_cpu, usage.CpuPercent);
        Push(_memory, usage.MemoryBytes);
        CpuHistory = _cpu.ToArray();
        MemoryHistory = _memory.ToArray();
        CpuText = string.Format(Strings.Dashboard_Cpu_Value, usage.CpuPercent);
        MemoryText = FormatBytes(usage.MemoryBytes);
    }

    internal static string FormatBytes(long bytes) => bytes >= Gigabyte
        ? string.Format(Strings.Dashboard_Memory_Gigabytes, bytes / Gigabyte)
        : string.Format(Strings.Dashboard_Memory_Megabytes, bytes / Megabyte);

    private static void Push(List<double> history, double value)
    {
        history.Add(value);
        if (history.Count > HistoryLength) history.RemoveAt(0);
    }

    private void ResetUsage()
    {
        _monitor.Reset();
        _cpu.Clear();
        _memory.Clear();
        CpuHistory = Array.Empty<double>();
        MemoryHistory = Array.Empty<double>();
        CpuText = Strings.Common_NotAvailable;
        MemoryText = Strings.Common_NotAvailable;
    }

    private void RefreshPlayers()
    {
        var online = _players.Players
            .Where(p => p.Status is PlayerStatus.Online or PlayerStatus.Joining)
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // Rebuild only on a change, so the list doesn't flicker every second.
        if (!online.SequenceEqual(OnlinePlayers))
        {
            OnlinePlayers.Clear();
            foreach (var row in online) OnlinePlayers.Add(row);
        }

        HasOnlinePlayers = online.Count > 0;
        _peakPlayers = Math.Max(_peakPlayers, online.Count);
        PlayersOnlineText = online.Count.ToString();
        PlayersCaption = string.Format(Strings.Dashboard_Players_Caption, _peakPlayers, _players.Players.Count);
    }

    private void RefreshBackup()
    {
        var latest = _backups.Backups.MaxBy(b => b.CreatedAt);
        LastBackupText = latest is null ? Strings.Dashboard_Backup_None : RelativeTime.Format(latest.CreatedAt, DateTimeOffset.Now);
        BackupsCaption = _backups.CurrentWorld is null
            ? Strings.Backups_World_None
            : string.Format(Strings.Dashboard_Backup_Caption, _backups.Backups.Count, _backups.CurrentWorld);
    }

    protected override void DisposeCore() => _timer.Stop();
}
