using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Game;
using ValheimServerGUI.Game.WorldMap;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Logging;
using Xunit;

namespace ValheimServerGUI.App.Tests.ViewModels;

/// <summary>
/// The World Map tab over a real temp save folder: loading the selected world's seed and locations, the category
/// filters, the location list, the no-world and no-save states, and previewing a typed seed.
/// </summary>
public sealed class WorldMapViewModelTests : IDisposable
{
    private static readonly IServiceProvider Core =
        new ServiceCollection().AddValheimCore().BuildServiceProvider();

    private const string Seed = "j3QV2ftr3y";

    private readonly DirectoryInfo _saveFolder;
    private string? _world = "Viking";

    public WorldMapViewModelTests()
    {
        _saveFolder = new DirectoryInfo(Path.Join(Path.GetTempPath(), "vsg_map_vm_" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Join(_saveFolder.FullName, "worlds_local"));
    }

    public void Dispose()
    {
        if (_saveFolder.Exists) _saveFolder.Delete(true);
    }

    private WorldMapViewModel Build()
        => new(Core.GetRequiredService<IApplicationLogger>(), () => _saveFolder, () => _world);

    private void WriteTable(params (float X, float Z)[] shared) => WriteTable(Array.Empty<TablePin>(), shared);

    private void WriteTable(TablePin[] pins, params (float X, float Z)[] shared)
        => File.WriteAllBytes(Path.Join(_saveFolder.GetLocalWorldFolder("Viking").FullName, "20_20__0_1.chunk"),
            SharedMapFixtures.BuildChunkOf(new[] { SharedMapFixtures.SharedMap(shared.Select(p => SharedMapFixtures.Pixel(p.X, p.Z)), pins) }));

    private void WriteWorld()
        => WorldMapSaves.Write(_saveFolder.GetLocalWorldFolder("Viking"), Seed,
            new[]
            {
                ("StartTemple", 5f, -3f),
                ("Eikthyrnir", 300f, 400f),
                ("GDKing", -1200f, 900f),
                ("Vendor_BlackForest", 2000f, 0f),
                ("Crypt2", 800f, 800f),
                ("GoblinCamp2", 4000f, 4000f),
                ("WoodHouse1", 10f, 10f),
            },
            new (short, short)[] { (0, 0), (1, 0) });

    [AvaloniaFact]
    public async Task Load_ReadsSeedAndLocationsFromTheLatestSave()
    {
        WriteWorld();
        var vm = Build();

        await vm.LoadSelectedWorldAsync();

        Assert.Equal("Viking", vm.WorldText);
        Assert.Equal(Seed, vm.SeedText);
        Assert.NotNull(vm.Renderer);
        Assert.Equal(ValheimHash.SeedFromName(Seed), vm.Renderer!.Seed);
        // Only the discovered map shows by default; with no character save, that is the server's generated zones.
        Assert.True(vm.ShowFog);
        Assert.Equal(Strings.Map_Exploration_ServerZones, Assert.Single(vm.ExplorationSources).DisplayName);
        Assert.Equal(new[] { WorldLocationKind.Spawn }, vm.Markers.Select(m => m.Kind));

        // Dungeons and camps start hidden; the unknown WoodHouse1 is never a marker.
        vm.ShowFog = false;
        Assert.Equal(
            new[] { WorldLocationKind.Spawn, WorldLocationKind.Boss, WorldLocationKind.Boss, WorldLocationKind.Trader },
            vm.Markers.Select(m => m.Kind));
        Assert.False(vm.IsBusy);
        Assert.StartsWith(Strings.Map_Status_World.Split('{')[0], vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Filters_ShowAndHideCategories()
    {
        WriteWorld();
        var vm = Build();
        await vm.LoadSelectedWorldAsync();
        vm.ShowFog = false;

        vm.ShowBosses = false;
        vm.ShowDungeons = true;
        vm.ShowCamps = true;

        Assert.Equal(
            new[] { WorldLocationKind.Spawn, WorldLocationKind.Trader, WorldLocationKind.Dungeon, WorldLocationKind.Camp },
            vm.Markers.Select(m => m.Kind));
    }

    [AvaloniaFact]
    public async Task Search_ListsBossesAndTradersByDefault_AndFindsByName()
    {
        WriteWorld();
        var vm = Build();
        await vm.LoadSelectedWorldAsync();
        vm.ShowFog = false;

        Assert.Equal(
            new[] { Strings.MapLocation_StartTemple, Strings.MapLocation_Eikthyr, Strings.MapLocation_TheElder, Strings.MapLocation_Haldor },
            vm.SearchResults.Select(r => r.Name));

        vm.SearchText = Strings.MapLocation_BurialChambers;
        var row = Assert.Single(vm.SearchResults);

        vm.SelectedResult = row;
        Assert.NotNull(vm.Focus);
        Assert.Equal(800f, vm.Focus!.X);
        Assert.Equal(800f, vm.Focus.Z);
    }

    [AvaloniaFact]
    public async Task Load_NoWorldSelected_ShowsNothing()
    {
        _world = null;
        var vm = Build();

        await vm.LoadSelectedWorldAsync();

        Assert.Null(vm.Renderer);
        Assert.Equal(Strings.Map_World_None, vm.WorldText);
        Assert.Equal(Strings.Map_Status_NoWorld, vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Load_WorldWithoutSave_SaysSo()
    {
        var vm = Build();

        await vm.LoadSelectedWorldAsync();

        Assert.Null(vm.Renderer);
        Assert.Equal(Strings.Map_Status_NoSave, vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Preview_DrawsTheTypedSeedWithoutLocations()
    {
        WriteWorld();
        var vm = Build();
        await vm.LoadSelectedWorldAsync();

        vm.PreviewSeed = "  HHcLC5acQt ";
        await vm.PreviewCommand.ExecuteAsync(null);

        Assert.Equal(ValheimHash.SeedFromName("HHcLC5acQt"), vm.Renderer!.Seed);
        Assert.Equal("HHcLC5acQt", vm.SeedText);
        Assert.Empty(vm.Markers);
        Assert.Null(vm.Exploration);
        Assert.Empty(vm.ExplorationSources);
        Assert.Equal(Strings.Map_Status_Preview, vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Load_ShowsTheCartographyTablesMap()
    {
        WriteWorld();
        WriteTable((300f, 400f), (5f, -3f));
        var vm = Build();

        await vm.LoadSelectedWorldAsync();

        Assert.Equal(new[] { Strings.Map_Exploration_Tables_One, Strings.Map_Exploration_ServerZones },
            vm.ExplorationSources.Select(s => s.DisplayName));
        Assert.Same(vm.ExplorationSources[0].Exploration, vm.Exploration);
        // Eikthyr's altar is on the table's map; the Elder, Haldor and the rest are not.
        Assert.Equal(new[] { WorldLocationKind.Spawn, WorldLocationKind.Boss }, vm.Markers.Select(m => m.Kind));
        Assert.Equal(new[] { Strings.MapLocation_StartTemple, Strings.MapLocation_Eikthyr }, vm.SearchResults.Select(r => r.Name));

        vm.SelectedExplorationSource = vm.ExplorationSources[1];
        Assert.Equal(new[] { WorldLocationKind.Spawn }, vm.Markers.Select(m => m.Kind));

        // A reload keeps the server zones selected.
        await vm.LoadSelectedWorldAsync();
        Assert.True(vm.SelectedExplorationSource!.IsGeneratedZones);
    }

    [AvaloniaFact]
    public async Task Load_ShowsTheTablesPins()
    {
        WriteWorld();
        WriteTable(new[]
        {
            new TablePin("MAISON", 50, 60, Type: 1),
            new TablePin("Troll", 700, -200, Checked: true),
            new TablePin("$enemy_eikthyr", 300, 400, Type: 9), // its altar is on the map already
            new TablePin("$enemy_gdking", -1200, 900, Type: 9), // its altar is not discovered
        }, (300f, 400f), (5f, -3f));
        var vm = Build();

        await vm.LoadSelectedWorldAsync();

        var pins = vm.Markers.Where(m => m.Kind == WorldLocationKind.Pin).ToList();
        Assert.Equal(new[] { "MAISON", "Troll", Strings.MapLocation_TheElder }, pins.Select(p => p.Name));
        Assert.Equal(new[] { WorldMapPinIcon.Home, WorldMapPinIcon.Dot, WorldMapPinIcon.Boss }, pins.Select(p => p.PinIcon));
        Assert.True(pins[1].IsChecked);
        // The list holds the named pins after the places, nearest the spawn first.
        Assert.Equal(
            new[] { Strings.MapLocation_StartTemple, Strings.MapLocation_Eikthyr, "MAISON", "Troll", Strings.MapLocation_TheElder },
            vm.SearchResults.Select(r => r.Name));

        // The pins show whatever the discovered area comes from (in the server's zones, Eikthyr's altar is not, so its
        // pin shows), and can be hidden.
        vm.SelectedExplorationSource = vm.ExplorationSources[1];
        Assert.Equal(4, vm.Markers.Count(m => m.Kind == WorldLocationKind.Pin));
        vm.ShowPins = false;
        Assert.DoesNotContain(vm.Markers, m => m.Kind == WorldLocationKind.Pin);
    }

    [AvaloniaFact]
    public async Task Publishing_ServesTheMapAsShown()
    {
        WriteWorld();
        WriteTable((300f, 400f), (5f, -3f));
        using var server = new WorldMapWebServer();
        var prefs = new FakeUserPreferencesProvider();
        var port = FreePort();
        var vm = new WorldMapViewModel(Core.GetRequiredService<IApplicationLogger>(), () => _saveFolder, () => _world,
            server, prefs, () => "203.0.113.7");
        vm.PublishPort = port;

        vm.IsPublishing = true;
        for (var i = 0; i < 400 && (vm.Renderer is null || vm.IsBusy || !server.Respond("GET /map.png HTTP/1.1").AsSpan(0, 12).SequenceEqual("HTTP/1.1 200"u8)); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(server.IsRunning, vm.StatusMessage);
        Assert.Equal($"http://203.0.113.7:{port}/", vm.PublicLink);
        Assert.Equal($"http://localhost:{port}/", vm.LocalLink);
        var page = System.Text.Encoding.UTF8.GetString(server.Respond("GET / HTTP/1.1"));
        Assert.Contains(System.Net.WebUtility.HtmlEncode(string.Format(Strings.MapWeb_Title, "Viking")), page);
        // Place names are in the page's script data, JSON-encoded.
        static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);
        Assert.Contains(Json(Strings.MapLocation_Eikthyr), page);
        Assert.DoesNotContain(Json(Strings.MapLocation_TheElder), page);
        Assert.True(prefs.LoadPreferences().PublishMapOnWeb);
        Assert.Equal(port, prefs.LoadPreferences().MapWebPort);

        vm.IsPublishing = false;
        Assert.False(server.IsRunning);
        Assert.Null(vm.PublicLink);
        Assert.False(prefs.LoadPreferences().PublishMapOnWeb);
    }

    [AvaloniaFact]
    public void Publishing_ReportsAPortInUse()
    {
        using var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        blocker.Start();
        var port = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;
        using var server = new WorldMapWebServer();
        var vm = new WorldMapViewModel(Core.GetRequiredService<IApplicationLogger>(), () => _saveFolder, () => _world, server);
        vm.PublishPort = port;

        vm.IsPublishing = true;

        Assert.False(vm.IsPublishing);
        Assert.False(server.IsRunning);
        var expected = string.Format(Strings.Map_Status_PublishFailed, port, "\0");
        Assert.StartsWith(expected[..expected.IndexOf('\0')], vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Players_NameTheServerPathThatWasNotFound()
    {
        WriteWorld();
        var exe = Path.Join(_saveFolder.FullName, "missing", "valheim_server.exe");
        using var web = new WorldMapWebServer();
        var vm = new WorldMapViewModel(Core.GetRequiredService<IApplicationLogger>(), () => _saveFolder, () => _world,
            web, serverExePath: () => exe);

        await vm.RefreshPlayersAsync();
        Assert.Equal(ServerPluginState.NoServer, vm.PluginState);
        Assert.Equal(string.Format(Strings.Map_Players_Status_NotFound, exe), vm.PlayersStatus);

        exe = "";
        await vm.RefreshPlayersAsync();
        Assert.Equal(Strings.Map_Players_Status_NoServer, vm.PlayersStatus);
    }

    [AvaloniaFact]
    public async Task Players_ExpandEnvironmentVariablesInTheServerPath()
    {
        WriteWorld();
        var server = Directory.CreateDirectory(Path.Join(_saveFolder.FullName, "server"));
        File.WriteAllText(Path.Join(server.FullName, "valheim_server.exe"), "");
        Environment.SetEnvironmentVariable("VSG_TEST_SERVER_DIR", server.FullName);
        try
        {
            using var web = new WorldMapWebServer();
            var vm = new WorldMapViewModel(Core.GetRequiredService<IApplicationLogger>(), () => _saveFolder, () => _world,
                web, serverExePath: () => Path.Join("%VSG_TEST_SERVER_DIR%", "valheim_server.exe"));

            await vm.RefreshPlayersAsync();
            Assert.Equal(ServerPluginState.NoBepInEx, vm.PluginState);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VSG_TEST_SERVER_DIR", null);
        }
    }

    [AvaloniaFact]
    public async Task Players_ShowOnceThePluginRunsOnTheServer()
    {
        WriteWorld();
        var server = Directory.CreateDirectory(Path.Join(_saveFolder.FullName, "server"));
        var exe = Path.Join(server.FullName, "valheim_server.exe");
        File.WriteAllText(exe, "");
        using var web = new WorldMapWebServer();
        var vm = new WorldMapViewModel(Core.GetRequiredService<IApplicationLogger>(), () => _saveFolder, () => _world,
            web, serverExePath: () => exe);
        await vm.LoadSelectedWorldAsync();

        await vm.RefreshPlayersAsync();
        Assert.Equal(ServerPluginState.NoBepInEx, vm.PluginState);
        Assert.Equal(Strings.Map_Players_Status_NoBepInEx, vm.PlayersStatus);
        Assert.False(vm.CanInstallPlugin);

        var core = Directory.CreateDirectory(Path.Join(server.FullName, "BepInEx", "core"));
        File.WriteAllText(Path.Join(core.FullName, "BepInEx.dll"), "");
        await vm.RefreshPlayersAsync();
        Assert.True(vm.CanInstallPlugin);

        await vm.InstallPluginCommand.ExecuteAsync(null);
        Assert.Equal(ServerPluginState.Installed, vm.PluginState);
        Assert.Equal(Strings.Map_Players_Status_Installed, vm.PlayersStatus);
        Assert.Empty(vm.Players);

        void WritePlayers(string world, DateTime time) => File.WriteAllText(LivePlayerReader.FilePath(exe),
            "{\"version\":1,\"time\":\"" + time.ToString("o") + "\",\"world\":\"" + world + "\",\"players\":[" +
            "{\"name\":\"Ragnar\",\"x\":300,\"z\":400,\"public\":true},{\"name\":\"Lagertha\",\"x\":-10,\"z\":5,\"public\":false}]}");
        Directory.CreateDirectory(Path.GetDirectoryName(LivePlayerReader.FilePath(exe))!);

        WritePlayers("Viking", DateTime.UtcNow);
        await vm.RefreshPlayersAsync();
        Assert.Equal(new[] { "Ragnar", "Lagertha" }, vm.Players.Select(p => p.Name));
        Assert.Equal(string.Format(Strings.Map_Players_Status_Other, 2), vm.PlayersStatus);

        vm.SelectedPlayer = vm.Players[0];
        Assert.Equal(300f, vm.Focus!.X);

        vm.ShowPlayers = false;
        Assert.Empty(vm.Players);
        vm.ShowPlayers = true;

        // Another world's players, or a file the stopped server left behind, do not count.
        WritePlayers("Elsewhere", DateTime.UtcNow);
        await vm.RefreshPlayersAsync();
        Assert.Empty(vm.Players);
        WritePlayers("Viking", DateTime.UtcNow.AddMinutes(-5));
        await vm.RefreshPlayersAsync();
        Assert.Empty(vm.Players);
        Assert.Equal(Strings.Map_Players_Status_Waiting, vm.PlayersStatus);
    }

    [AvaloniaFact]
    public async Task Players_OnTheWebPage_AreOnlyThoseVisibleOnTheInGameMap()
    {
        WriteWorld();
        var server = Directory.CreateDirectory(Path.Join(_saveFolder.FullName, "server"));
        var exe = Path.Join(server.FullName, "valheim_server.exe");
        File.WriteAllText(exe, "");
        Directory.CreateDirectory(Path.Join(server.FullName, "BepInEx", "core"));
        File.WriteAllText(Path.Join(server.FullName, "BepInEx", "core", "BepInEx.dll"), "");
        ServerPluginInstaller.Install(exe);
        Directory.CreateDirectory(Path.GetDirectoryName(LivePlayerReader.FilePath(exe))!);
        File.WriteAllText(LivePlayerReader.FilePath(exe),
            "{\"time\":\"" + DateTime.UtcNow.ToString("o") + "\",\"world\":\"Viking\",\"players\":[" +
            "{\"name\":\"Ragnar\",\"x\":300,\"z\":400,\"public\":true},{\"name\":\"Lagertha\",\"x\":-10,\"z\":5,\"public\":false}]}");
        using var web = new WorldMapWebServer();
        var vm = new WorldMapViewModel(Core.GetRequiredService<IApplicationLogger>(), () => _saveFolder, () => _world,
            web, serverExePath: () => exe) { PublishPort = FreePort() };
        await vm.LoadSelectedWorldAsync();

        vm.IsPublishing = true;
        string players = "";
        for (var i = 0; i < 200 && !players.Contains("Ragnar"); i++)
        {
            await Task.Delay(20);
            players = System.Text.Encoding.UTF8.GetString(web.Respond("GET /players HTTP/1.1"));
        }

        Assert.Contains("Ragnar", players);
        Assert.DoesNotContain("Lagertha", players);
        Assert.Equal(2, vm.Players.Count);
        vm.IsPublishing = false;
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
