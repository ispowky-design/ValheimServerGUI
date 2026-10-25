using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerGUI.App.Services;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Game;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Logging;
using Xunit;

namespace ValheimServerGUI.App.Tests.ViewModels;

/// <summary>
/// Force Stop is offered, never automatic: it appears only once the selected server has been stopping for the
/// Preferences delay, asks before killing, and disappears when the server stops.
/// </summary>
public sealed class ForceStopOfferTests : IDisposable
{
    private static readonly IServiceProvider Core =
        new ServiceCollection().AddValheimCore().BuildServiceProvider();

    private readonly string _dir;
    private readonly string _exe;
    private readonly string _saveDir;
    private readonly FakeProcessProvider _processes = new();
    private readonly FakeUserPreferencesProvider _userPrefs = new(new UserPreferences { ForceStopOfferSeconds = 60 });
    private DateTimeOffset _now = DateTimeOffset.Now;

    public ForceStopOfferTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vsg-forcestop-" + Guid.NewGuid().ToString("N"));
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
        var shell = new ShellLauncher(new Services.RecordingSystemShell(), TestLog.Silent);
        var baselines = new InMemoryBaselineStore();
        var accessLists = Core.GetRequiredService<IPlayerAccessListService>();
        var import = new PlayerListImportService(accessLists, baselines);
        var manager = new ServerManager(
            () => new ValheimServer(_processes, Core.GetRequiredService<IPlayerDataRepository>(),
                Core.GetRequiredService<IApplicationLogger>(), Core.GetRequiredService<IValheimPathResolver>(),
                accessLists, import, baselines),
            Core.GetRequiredService<Serilog.ILogger>());

        var vm = new MainWindowViewModel(
            manager, _userPrefs, new FakeServerPreferencesProvider(new[] { new ServerPreferences { ProfileName = "A" } }),
            Core.GetRequiredService<IWorldPreferencesProvider>(), new FakeSteamCloudWorldProvider(),
            Core.GetRequiredService<IIpAddressProvider>(), Core.GetRequiredService<IPlayerDataRepository>(),
            Core.GetRequiredService<IApplicationLogger>(), new FakeSoftwareUpdateProvider(), shell,
            Core.GetRequiredService<IValheimPathResolver>(), import);
        vm.Now = () => _now;
        vm.LoadProfile(new ServerPreferences { ProfileName = "A" });
        return vm;
    }

    private void StartThenStop(MainWindowViewModel vm)
    {
        vm.Server!.Start(new ValheimServerOptions
        {
            Name = "Srv", WorldName = "Worldxx", Password = "hunter2", Port = 2456, SaveInterval = 30,
            Backups = 1, BackupShort = 60, BackupLong = 120, ServerExePath = _exe, SaveDataFolderPath = _saveDir, LogToFile = false,
        });
        vm.Server.Logger!.Information("Game server connected");
        vm.StopCommand.Execute(null);
        _now = vm.Server.StoppingSince!.Value;
    }

    [Fact]
    public void Not_offered_while_running_or_before_the_delay()
    {
        var vm = Build();
        Assert.False(vm.ForceStopAvailable);

        StartThenStop(vm);
        Assert.Equal(ServerStatus.Stopping, vm.ServerStatus);

        _now += TimeSpan.FromSeconds(59);
        vm.RefreshForceStopOffer();
        Assert.False(vm.ForceStopAvailable);
        Assert.False(vm.ForceStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Offered_after_the_delay_and_kills_only_after_confirming()
    {
        var vm = Build();
        StartThenStop(vm);
        _now += TimeSpan.FromSeconds(60);
        vm.RefreshForceStopOffer();
        Assert.True(vm.ForceStopCommand.CanExecute(null));

        var answer = false;
        string? asked = null;
        vm.ForceStopPrompt = msg =>
        {
            asked = msg;
            return Task.FromResult(answer);
        };

        await vm.ForceStopCommand.ExecuteAsync(null);
        Assert.NotNull(asked);
        Assert.Equal(0, _processes.ForceKillCount);

        answer = true;
        await vm.ForceStopCommand.ExecuteAsync(null);
        Assert.Equal(1, _processes.ForceKillCount);
    }

    [Fact]
    public void Withdrawn_once_the_server_has_stopped()
    {
        var vm = Build();
        StartThenStop(vm);
        _now += TimeSpan.FromMinutes(5);
        vm.RefreshForceStopOffer();
        Assert.True(vm.ForceStopAvailable);

        _processes.SimulateExit();

        Assert.Equal(ServerStatus.Stopped, vm.ServerStatus);
        Assert.False(vm.ForceStopAvailable);
    }
}
