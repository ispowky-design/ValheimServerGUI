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
        Assert.Equal(2, vm.ExploredZones!.Count);
        // Dungeons and camps start hidden; the unknown WoodHouse1 is never a marker.
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
        Assert.Null(vm.ExploredZones);
        Assert.Equal(Strings.Map_Status_Preview, vm.StatusMessage);
    }
}
