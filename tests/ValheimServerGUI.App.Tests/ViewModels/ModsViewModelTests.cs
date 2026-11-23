using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerGUI.App.Converters;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Game.Mods;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools.Logging;
using Xunit;

namespace ValheimServerGUI.App.Tests.ViewModels;

/// <summary>
/// The Mods tab over a scratch server folder and an in-memory catalog: only server-only mods are listed (refused ones
/// on request, never installable), install brings BepInExPack along, the running-server gate, disable and remove.
/// </summary>
public sealed class ModsViewModelTests : IDisposable
{
    private static readonly IServiceProvider Core = new ServiceCollection().AddValheimCore().BuildServiceProvider();

    private readonly string _server = Path.Join(Path.GetTempPath(), "vsg_mods_vm_" + Guid.NewGuid().ToString("N"));
    private readonly Catalog _catalog = new();
    private readonly RecordingShellLauncher _shell = new();
    private bool _inUse;
    private bool _confirm = true;
    private string? _error;

    public ModsViewModelTests()
    {
        Directory.CreateDirectory(_server);
        File.WriteAllText(Exe, "server");
        _catalog.Add(Package(ModSafety.BepInExPackFullName, new[] { "Libraries" }, "BepInEx pack for Valheim."),
            Zip(("BepInExPack_Valheim/BepInEx/core/BepInEx.dll", "bepinex"), ("BepInExPack_Valheim/winhttp.dll", "doorstop")));
        _catalog.Add(Package("Owner-WorldBackup", new[] { "Server-side" }, "Backs up the world on the server.", downloads: 500),
            Zip(("WorldBackup.dll", "dll")));
        _catalog.Add(Package("Owner-ClientMap", new[] { "Client-side", "Server-side" }, "Shared map.", downloads: 900),
            Zip(("ClientMap.dll", "dll")));
        _catalog.Add(Package("Owner-Untagged", new[] { "Tweaks" }, "Something.", downloads: 50), Zip(("U.dll", "dll")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_server)) Directory.Delete(_server, true);
    }

    private string Exe => Path.Join(_server, "valheim_server.exe");

    private async Task<ModsViewModel> Build()
    {
        var vm = new ModsViewModel(_catalog, new ServerModManager(_catalog, Core.GetRequiredService<IApplicationLogger>()), _shell,
            Core.GetRequiredService<IApplicationLogger>(), () => Exe, _ => _inUse)
        {
            ConfirmPrompt = (_, _) => Task.FromResult(_confirm),
            ErrorReported = msg => _error = msg,
        };
        vm.SetActive(true);
        await vm.LoadCatalogAsync(forceRefresh: false);
        return vm;
    }

    [AvaloniaFact]
    public async Task Lists_only_server_only_mods_by_default()
    {
        var vm = await Build();

        Assert.Equal(new[] { "Owner-WorldBackup", ModSafety.BepInExPackFullName }, vm.Results.Select(r => r.Package.FullName));
        Assert.All(vm.Results, r => Assert.True(r.IsAllowed));
        Assert.Equal(EnumDisplayConverter.ToText(ModSafetyVerdict.ServerOnly), vm.Results[0].VerdictText);
        Assert.Equal(Strings.Mods_BepInEx_Missing, vm.BepInExText);
    }

    [AvaloniaFact]
    public async Task Refused_mods_show_on_request_with_the_reason_and_cannot_be_installed()
    {
        var vm = await Build();
        vm.ShowRefused = true;

        var client = Assert.Single(vm.Results, r => r.Package.FullName == "Owner-ClientMap");
        Assert.False(client.IsAllowed);
        Assert.Equal(Strings.Mods_Reason_ClientSideTag, client.ReasonText);
        Assert.Equal(Strings.Mods_Reason_NoServerSideTag, Assert.Single(vm.Results, r => r.Package.FullName == "Owner-Untagged").ReasonText);

        vm.SelectedResult = client;
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Search_matches_name_owner_and_description()
    {
        var vm = await Build();
        vm.SearchText = "world";
        Assert.Equal("Owner-WorldBackup", Assert.Single(vm.Results).Package.FullName);
        vm.SearchText = "nothing like this";
        Assert.Empty(vm.Results);
        Assert.Equal(Strings.Mods_Catalog_Empty, vm.ResultsEmptyText);
    }

    [AvaloniaFact]
    public async Task Install_brings_BepInEx_along_and_lists_both()
    {
        var vm = await Build();
        vm.SelectedResult = vm.Results.First(r => r.Package.FullName == "Owner-WorldBackup");

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Null(_error);
        Assert.True(File.Exists(Path.Join(_server, "BepInEx", "plugins", "Owner-WorldBackup", "WorldBackup.dll")));
        Assert.True(File.Exists(Path.Join(_server, "winhttp.dll")));
        Assert.Equal(new[] { ModSafety.BepInExPackFullName, "Owner-WorldBackup" }, vm.Installed.Select(i => i.FullName));
        Assert.Equal(Strings.Mods_BepInEx_Installed, vm.BepInExText);
        Assert.Equal(string.Format(Strings.Mods_Status_Installed, "WorldBackup"), vm.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Declining_the_prompt_installs_nothing()
    {
        _confirm = false;
        var vm = await Build();
        vm.SelectedResult = vm.Results.First(r => r.Package.FullName == "Owner-WorldBackup");

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Empty(_catalog.Downloads);
        Assert.Empty(vm.Installed);
    }

    [AvaloniaFact]
    public async Task Nothing_changes_while_a_server_runs_from_the_folder()
    {
        _inUse = true;
        var vm = await Build();
        vm.SelectedResult = vm.Results.First(r => r.Package.FullName == "Owner-WorldBackup");

        Assert.True(vm.IsFolderInUse);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_server_started_after_the_list_was_drawn_still_blocks_the_install()
    {
        var vm = await Build();
        vm.SelectedResult = vm.Results.First(r => r.Package.FullName == "Owner-WorldBackup");
        _inUse = true;

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Mods_InUse, _error);
        Assert.Empty(_catalog.Downloads);
    }

    [AvaloniaFact]
    public async Task Disable_then_remove_an_installed_mod()
    {
        var vm = await Build();
        vm.SelectedResult = vm.Results.First(r => r.Package.FullName == "Owner-WorldBackup");
        await vm.InstallCommand.ExecuteAsync(null);

        vm.SelectedInstalled = vm.Installed.Single(i => i.FullName == "Owner-WorldBackup");
        Assert.Equal(Strings.Mods_Disable, vm.ToggleEnabledText);
        await vm.ToggleEnabledCommand.ExecuteAsync(null);
        var row = vm.Installed.Single(i => i.FullName == "Owner-WorldBackup");
        Assert.False(row.IsEnabled);
        Assert.Equal(Strings.Mods_State_Disabled, row.StateText);

        vm.SelectedInstalled = row;
        await vm.RemoveCommand.ExecuteAsync(null);
        Assert.DoesNotContain(vm.Installed, i => i.FullName == "Owner-WorldBackup");
        Assert.False(Directory.Exists(Path.Join(_server, "BepInEx", "plugins", "Owner-WorldBackup")));

        // BepInEx stays and cannot be removed or turned off from here.
        vm.SelectedInstalled = vm.Installed.Single();
        Assert.True(vm.SelectedInstalled.IsLoader);
        Assert.False(vm.RemoveCommand.CanExecute(null));
        Assert.False(vm.ToggleEnabledCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task An_unreachable_Thunderstore_is_reported_in_the_status()
    {
        _catalog.FailWith = new System.Net.Http.HttpRequestException("offline");
        var vm = await Build();

        Assert.Equal(string.Format(Strings.Mods_Catalog_Failed, "offline"), vm.CatalogStatus);
        Assert.Equal(Strings.Mods_Catalog_NotLoaded, vm.ResultsEmptyText);
    }

    // ----- helpers -----

    private static ThunderstorePackage Package(string fullName, string[] categories, string description, long downloads = 10)
    {
        var dash = fullName.IndexOf('-');
        return new ThunderstorePackage(fullName[..dash], fullName[(dash + 1)..], categories, false, false,
            "https://thunderstore.io/c/valheim/p/" + fullName.Replace('-', '/') + "/", DateTime.UtcNow, downloads, 0,
            new ThunderstoreVersion("1.0.0", description, Array.Empty<string>(), "https://thunderstore.io/package/download/x/", 10, null, null));
    }

    private static byte[] Zip(params (string Path, string Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private sealed class Catalog : IThunderstoreCatalog
    {
        private readonly List<ThunderstorePackage> _packages = new();
        private readonly Dictionary<string, byte[]> _zips = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Downloads { get; } = new();

        public Exception? FailWith { get; set; }

        public void Add(ThunderstorePackage package, byte[] zip)
        {
            _packages.Add(package);
            _zips[package.FullName] = zip;
        }

        public Task<ThunderstoreCatalogSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
            => FailWith is not null
                ? Task.FromException<ThunderstoreCatalogSnapshot>(FailWith)
                : Task.FromResult(new ThunderstoreCatalogSnapshot(_packages.ToList(), DateTime.UtcNow, false));

        public Task<byte[]> DownloadAsync(ThunderstorePackage package, CancellationToken cancellationToken)
        {
            Downloads.Add(package.FullName);
            return Task.FromResult(_zips[package.FullName]);
        }
    }
}
