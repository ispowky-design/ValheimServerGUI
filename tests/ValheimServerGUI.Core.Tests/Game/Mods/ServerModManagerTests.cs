using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game.Mods;
using Xunit;
using static ValheimServerGUI.Core.Tests.Game.Mods.ModTestPackages;

namespace ValheimServerGUI.Core.Tests.Game.Mods;

/// <summary>
/// Installing into a scratch server folder: the plan (dependencies first, BepInExPack only when missing, refusals),
/// the r2modman layout, the record of written files, update, disable/enable and removal with orphaned dependencies.
/// </summary>
public sealed class ServerModManagerTests : IDisposable
{
    private readonly string _server = Path.Join(Path.GetTempPath(), "vsg_mods_" + Guid.NewGuid().ToString("N"));
    private readonly FakeThunderstoreCatalog _catalog = new();
    private readonly ServerModManager _manager;

    public ServerModManagerTests()
    {
        Directory.CreateDirectory(_server);
        _manager = new ServerModManager(_catalog, new FakeApplicationLogger());
    }

    public void Dispose()
    {
        if (Directory.Exists(_server)) Directory.Delete(_server, true);
    }

    private string P(string relative) => Path.Join(_server, relative);

    private void GiveBepInEx()
    {
        Directory.CreateDirectory(P("BepInEx/core"));
        File.WriteAllText(P("BepInEx/core/BepInEx.dll"), "manual");
    }

    private async Task Install(ThunderstorePackage target)
    {
        var plan = ServerModManager.Plan(target, _catalog.Snapshot, _manager.GetInstalled(_server), ServerModManager.HasBepInEx(_server));
        await _manager.InstallAsync(_server, plan, _catalog.Snapshot, null, CancellationToken.None);
    }

    [Fact]
    public void Plan_PutsBepInExPackAndDependenciesFirst_WhenTheServerHasNoBepInEx()
    {
        var lib = Package("Lib-ServerLib", dependencies: new[] { "denikson-BepInExPack_Valheim-5.4.2202" });
        var mod = Package("Owner-Mod", dependencies: new[] { "Lib-ServerLib-1.0.0" });
        _catalog.Snapshot = Catalog(mod, lib, BepInExPack());

        var plan = ServerModManager.Plan(mod, _catalog.Snapshot, Array.Empty<InstalledMod>(), hasBepInEx: false);

        Assert.True(plan.IsAllowed);
        Assert.Equal(new[] { ModSafety.BepInExPackFullName, "Lib-ServerLib", "Owner-Mod" }, plan.Packages.Select(p => p.FullName));
    }

    [Fact]
    public void Plan_AddsBepInExPack_EvenWhenTheModDoesNotListIt()
    {
        var mod = Package("Owner-Mod");
        _catalog.Snapshot = Catalog(mod, BepInExPack());
        var plan = ServerModManager.Plan(mod, _catalog.Snapshot, Array.Empty<InstalledMod>(), hasBepInEx: false);
        Assert.Equal(new[] { ModSafety.BepInExPackFullName, "Owner-Mod" }, plan.Packages.Select(p => p.FullName));
    }

    [Fact]
    public void Plan_LeavesAHandInstalledBepInExAlone()
    {
        var mod = Package("Owner-Mod", dependencies: new[] { "denikson-BepInExPack_Valheim-5.4.2202" });
        _catalog.Snapshot = Catalog(mod, BepInExPack());
        var plan = ServerModManager.Plan(mod, _catalog.Snapshot, Array.Empty<InstalledMod>(), hasBepInEx: true);
        Assert.Equal(new[] { "Owner-Mod" }, plan.Packages.Select(p => p.FullName));
    }

    [Fact]
    public void Plan_RefusesAClientMod_AndPlansNothing()
    {
        var mod = Package("Owner-ClientMod", categories: new[] { "Client-side" });
        _catalog.Snapshot = Catalog(mod, BepInExPack());
        var plan = ServerModManager.Plan(mod, _catalog.Snapshot, Array.Empty<InstalledMod>(), hasBepInEx: false);
        Assert.False(plan.IsAllowed);
        Assert.Empty(plan.Packages);
    }

    [Fact]
    public async Task InstallAsync_RefusesAPlanWithAClientPackage_AndWritesNothing()
    {
        var client = Package("Owner-ClientMod", categories: new[] { "Client-side" });
        _catalog.Snapshot = Catalog(client);
        var forged = new ModInstallPlan(client, new ModSafetyResult(ModSafetyVerdict.ServerOnly, ModSafetyReason.ServerSideTag), new[] { client });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _manager.InstallAsync(_server, forged, _catalog.Snapshot, null, CancellationToken.None));
        Assert.Empty(_catalog.Downloads);
        Assert.False(Directory.Exists(P("BepInEx")));
    }

    [Fact]
    public async Task Install_LaysOutBepInExPackAndTheMod_AndRecordsThem()
    {
        var mod = Package("Owner-Mod", "1.2.0");
        _catalog.Snapshot = Catalog(mod, BepInExPack());
        _catalog.Zips[ModSafety.BepInExPackFullName] = BepInExPackZip();
        _catalog.Zips["Owner-Mod"] = Zip(
            ("manifest.json", "{}"), ("README.md", "readme"), ("icon.png", "png"),
            ("Mod.dll", "root dll"),
            ("plugins/Assets/data.json", "data"),
            ("config/Owner.Mod.cfg", "default config"),
            ("patchers/Patch.dll", "patcher"));

        await Install(mod);

        Assert.True(File.Exists(P("winhttp.dll")));
        Assert.True(File.Exists(P("doorstop_config.ini")));
        Assert.True(ServerModManager.HasBepInEx(_server));
        Assert.Equal("root dll", File.ReadAllText(P("BepInEx/plugins/Owner-Mod/Mod.dll")));
        Assert.True(File.Exists(P("BepInEx/plugins/Owner-Mod/Assets/data.json")));
        Assert.True(File.Exists(P("BepInEx/patchers/Owner-Mod/Patch.dll")));
        Assert.Equal("default config", File.ReadAllText(P("BepInEx/config/Owner.Mod.cfg")));
        Assert.False(File.Exists(P("BepInEx/plugins/Owner-Mod/manifest.json")));

        var installed = _manager.GetInstalled(_server);
        var record = Assert.Single(installed, m => m.FullName == "Owner-Mod");
        Assert.Equal("1.2.0", record.Version);
        Assert.False(record.AsDependency);
        Assert.True(record.Enabled);
        Assert.DoesNotContain(record.Files, f => f.StartsWith("BepInEx/config/", StringComparison.Ordinal));
        Assert.True(Assert.Single(installed, m => m.IsLoader).AsDependency);
    }

    [Fact]
    public async Task Install_KeepsAConfigTheServerAlreadyHas()
    {
        GiveBepInEx();
        Directory.CreateDirectory(P("BepInEx/config"));
        File.WriteAllText(P("BepInEx/config/Owner.Mod.cfg"), "tuned by the owner");
        var mod = Package("Owner-Mod");
        _catalog.Snapshot = Catalog(mod);
        _catalog.Zips["Owner-Mod"] = Zip(("Mod.dll", "dll"), ("config/Owner.Mod.cfg", "default"));

        await Install(mod);

        Assert.Equal("tuned by the owner", File.ReadAllText(P("BepInEx/config/Owner.Mod.cfg")));
    }

    [Fact]
    public async Task Install_RejectsAPathOutsideTheServerFolder()
    {
        GiveBepInEx();
        var mod = Package("Owner-Evil");
        _catalog.Snapshot = Catalog(mod);
        _catalog.Zips["Owner-Evil"] = Zip(("plugins/../../../../escape.dll", "x"));

        await Assert.ThrowsAsync<InvalidDataException>(() => Install(mod));
    }

    [Fact]
    public async Task Update_ReplacesTheOldFiles_AndKeepsTheModDisabled()
    {
        GiveBepInEx();
        var v1 = Package("Owner-Mod", "1.0.0");
        _catalog.Snapshot = Catalog(v1);
        _catalog.Zips["Owner-Mod"] = Zip(("Old.dll", "v1"));
        await Install(v1);
        _manager.SetEnabled(_server, "Owner-Mod", false);

        var v2 = Package("Owner-Mod", "2.0.0");
        _catalog.Snapshot = Catalog(v2);
        _catalog.Zips["Owner-Mod"] = Zip(("New.dll", "v2"));
        await Install(v2);

        Assert.False(File.Exists(P("BepInEx/plugins/Owner-Mod/Old.dll")));
        Assert.False(File.Exists(P("BepInEx/plugins/Owner-Mod/Old.dll.old")));
        Assert.True(File.Exists(P("BepInEx/plugins/Owner-Mod/New.dll.old")));
        var record = Assert.Single(_manager.GetInstalled(_server));
        Assert.Equal("2.0.0", record.Version);
        Assert.False(record.Enabled);
    }

    [Fact]
    public void Plan_SkipsWhatIsAlreadyUpToDate()
    {
        var lib = Package("Lib-ServerLib");
        var mod = Package("Owner-Mod", "2.0.0", dependencies: new[] { "Lib-ServerLib-1.0.0" });
        _catalog.Snapshot = Catalog(mod, lib);
        var installed = new[]
        {
            new InstalledMod { FullName = "Lib-ServerLib", Version = "1.0.0", AsDependency = true },
            new InstalledMod { FullName = "Owner-Mod", Version = "1.0.0" },
        };
        var plan = ServerModManager.Plan(mod, _catalog.Snapshot, installed, hasBepInEx: true);
        Assert.Equal(new[] { "Owner-Mod" }, plan.Packages.Select(p => p.FullName));
    }

    [Fact]
    public async Task SetEnabled_RenamesTheDlls_BackAndForth()
    {
        GiveBepInEx();
        var mod = Package("Owner-Mod");
        _catalog.Snapshot = Catalog(mod);
        _catalog.Zips["Owner-Mod"] = Zip(("Mod.dll", "dll"), ("data.txt", "data"));
        await Install(mod);

        _manager.SetEnabled(_server, "Owner-Mod", false);
        Assert.True(File.Exists(P("BepInEx/plugins/Owner-Mod/Mod.dll.old")));
        Assert.False(File.Exists(P("BepInEx/plugins/Owner-Mod/Mod.dll")));
        Assert.True(File.Exists(P("BepInEx/plugins/Owner-Mod/data.txt")));
        Assert.False(Assert.Single(_manager.GetInstalled(_server)).Enabled);

        _manager.SetEnabled(_server, "Owner-Mod", true);
        Assert.True(File.Exists(P("BepInEx/plugins/Owner-Mod/Mod.dll")));
        Assert.True(Assert.Single(_manager.GetInstalled(_server)).Enabled);
    }

    [Fact]
    public async Task Uninstall_RemovesTheModAndOrphanedDependencies_ButKeepsBepInExAndConfigs()
    {
        var lib = Package("Lib-ServerLib");
        var mod = Package("Owner-Mod", dependencies: new[] { "Lib-ServerLib-1.0.0" });
        var other = Package("Owner-Other", dependencies: new[] { "Lib-Shared-1.0.0" });
        var shared = Package("Lib-Shared");
        _catalog.Snapshot = Catalog(mod, lib, other, shared, BepInExPack());
        _catalog.Zips[ModSafety.BepInExPackFullName] = BepInExPackZip();
        _catalog.Zips["Lib-ServerLib"] = Zip(("Lib.dll", "lib"));
        _catalog.Zips["Owner-Mod"] = Zip(("Mod.dll", "mod"), ("config/Owner.Mod.cfg", "cfg"));
        _catalog.Zips["Owner-Other"] = Zip(("Other.dll", "other"));
        _catalog.Zips["Lib-Shared"] = Zip(("Shared.dll", "shared"));
        await Install(mod);
        await Install(other);

        _manager.Uninstall(_server, "Owner-Mod");

        Assert.False(Directory.Exists(P("BepInEx/plugins/Owner-Mod")));
        Assert.False(Directory.Exists(P("BepInEx/plugins/Lib-ServerLib")));
        Assert.True(File.Exists(P("BepInEx/plugins/Lib-Shared/Shared.dll")));
        Assert.True(File.Exists(P("BepInEx/config/Owner.Mod.cfg")));
        Assert.True(ServerModManager.HasBepInEx(_server));
        Assert.Equal(new[] { ModSafety.BepInExPackFullName, "Lib-Shared", "Owner-Other" },
            _manager.GetInstalled(_server).Select(m => m.FullName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.Throws<InvalidOperationException>(() => _manager.Uninstall(_server, ModSafety.BepInExPackFullName));
    }

    [Fact]
    public async Task UnmanagedPlugins_AreTheOnesTheAppDidNotInstall()
    {
        GiveBepInEx();
        Directory.CreateDirectory(P("BepInEx/plugins/HandMade"));
        Directory.CreateDirectory(P("BepInEx/plugins/ValheimServerGUI"));
        File.WriteAllText(P("BepInEx/plugins/Loose.dll"), "x");
        var mod = Package("Owner-Mod");
        _catalog.Snapshot = Catalog(mod);
        _catalog.Zips["Owner-Mod"] = Zip(("Mod.dll", "dll"));
        await Install(mod);

        Assert.Equal(new[] { "HandMade", "Loose.dll" }, _manager.GetUnmanagedPlugins(_server));
    }

    [Fact]
    public void IsServerRunningFrom_AFolderWithNoServer_IsFalse()
        => Assert.False(ServerModManager.IsServerRunningFrom(_server));
}
