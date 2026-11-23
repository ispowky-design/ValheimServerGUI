using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ValheimServerGUI.Tools.Logging;

namespace ValheimServerGUI.Game.Mods;

/// <summary>A mod the app installed into a server folder, as its record in <see cref="ServerModManager.ManifestPath"/> says.</summary>
public sealed class InstalledMod
{
    /// <summary><c>Owner-Name</c>.</summary>
    [JsonProperty("fullName")] public string FullName { get; set; } = "";
    [JsonProperty("version")] public string Version { get; set; } = "";
    [JsonProperty("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>Installed because another mod needs it, not picked by the user; removed with the last mod that needs it.</summary>
    [JsonProperty("asDependency")] public bool AsDependency { get; set; }
    [JsonProperty("installedAtUtc")] public DateTime InstalledAtUtc { get; set; }

    /// <summary>The <c>Owner-Name</c> ids of the packages it needs.</summary>
    [JsonProperty("dependencies")] public List<string> Dependencies { get; set; } = new();

    /// <summary>The files it wrote, relative to the server folder, with forward slashes.</summary>
    [JsonProperty("files")] public List<string> Files { get; set; } = new();

    [JsonIgnore] public bool IsLoader => ModSafety.IsLoader(FullName);
}

/// <summary>What installing (or updating) a mod takes: the packages to download, dependencies first, or why it is refused.</summary>
public sealed record ModInstallPlan(ThunderstorePackage Target, ModSafetyResult Safety, IReadOnlyList<ThunderstorePackage> Packages)
{
    public bool IsAllowed => Safety.IsAllowed;
}

/// <summary>
/// Installs Thunderstore mods into a dedicated server's BepInEx folder, the way mod managers lay them out
/// (<c>BepInEx/plugins/Owner-Name/…</c>), and keeps a record of what it wrote so that it can update, disable
/// (<c>.dll</c> → <c>.dll.old</c>, as r2modman does) and remove them again. BepInExPack is installed into the server
/// folder when a mod needs it and the server has no BepInEx yet.
/// <para>
/// Every install re-checks <see cref="ModSafety"/> for the mod and each dependency, and refuses unless all of them
/// are server-only. Callers make sure no server runs from the folder (<see cref="IsServerRunningFrom"/>).
/// </para>
/// </summary>
public sealed class ServerModManager
{
    private const string ManifestFileName = "ValheimServerGUI.mods.json";
    private const string DisabledSuffix = ".old";

    // Files at a package's root that describe the package, not the mod.
    private static readonly HashSet<string> PackageMetaFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json", "icon.png", "README.md", "CHANGELOG.md", "LICENSE", "LICENSE.md", "LICENSE.txt",
    };

    private readonly IThunderstoreCatalog _catalog;
    private readonly IApplicationLogger _logger;

    public ServerModManager(IThunderstoreCatalog catalog, IApplicationLogger logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public static string ManifestPath(string serverFolder) => Path.Join(serverFolder, "BepInEx", ManifestFileName);

    public static string PluginsFolder(string serverFolder) => Path.Join(serverFolder, "BepInEx", "plugins");

    /// <summary>The server folder can load mods (BepInEx is there, however it was installed).</summary>
    public static bool HasBepInEx(string serverFolder) => File.Exists(Path.Join(serverFolder, "BepInEx", "core", "BepInEx.dll"));

    // ----- reading -----

    public IReadOnlyList<InstalledMod> GetInstalled(string serverFolder)
    {
        var path = ManifestPath(serverFolder);
        if (!File.Exists(path)) return Array.Empty<InstalledMod>();
        try
        {
            var mods = JsonConvert.DeserializeObject<List<InstalledMod>>(File.ReadAllText(path)) ?? new List<InstalledMod>();
            return mods.Where(m => !string.IsNullOrWhiteSpace(m.FullName)).ToList();
        }
        catch (JsonException e)
        {
            _logger.Warning("Could not read the installed mods record {path}: {message}", path, e.Message);
            return Array.Empty<InstalledMod>();
        }
    }

    /// <summary>
    /// The entries of <c>BepInEx/plugins</c> the app did not install (nor its own map plugin): mods put there by hand
    /// or by another mod manager, which the app cannot vouch for.
    /// </summary>
    public IReadOnlyList<string> GetUnmanagedPlugins(string serverFolder)
    {
        var plugins = PluginsFolder(serverFolder);
        if (!Directory.Exists(plugins)) return Array.Empty<string>();

        var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { WorldMap.ServerPluginInstaller.FolderName };
        foreach (var mod in GetInstalled(serverFolder))
        {
            foreach (var file in mod.Files)
            {
                var parts = file.Split('/');
                if (parts.Length >= 3 && parts[0] == "BepInEx" && parts[1] == "plugins") owned.Add(parts[2]);
            }
        }

        return Directory.EnumerateFileSystemEntries(plugins)
            .Select(Path.GetFileName)
            .Where(n => n is not null && !owned.Contains(n!))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Whether a Valheim dedicated server process runs from <paramref name="serverFolder"/>, whoever started it (this
    /// app, another tool, a console). A server process whose path cannot be read counts as running there, to stay safe.
    /// </summary>
    public static bool IsServerRunningFrom(string serverFolder)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverFolder)) + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var name in new[] { "valheim_server", "valheim_server.x86_64" })
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch (Exception) { continue; }

            foreach (var process in processes)
            {
                using (process)
                {
                    var path = ProcessImagePath.Get(process);
                    if (path is null || path.StartsWith(root, comparison)) return true;
                }
            }
        }
        return false;
    }

    // ----- planning -----

    /// <summary>
    /// What installing <paramref name="target"/> takes on a server with <paramref name="installed"/> mods: the packages
    /// that are missing or out of date, dependencies first. BepInExPack is included only when the server has no BepInEx.
    /// </summary>
    public static ModInstallPlan Plan(ThunderstorePackage target, ThunderstoreCatalogSnapshot catalog, IReadOnlyList<InstalledMod> installed, bool hasBepInEx)
    {
        var safety = catalog.Classify(target);
        if (!safety.IsAllowed)
        {
            return new ModInstallPlan(target, safety, Array.Empty<ThunderstorePackage>());
        }

        var versions = installed.ToDictionary(m => m.FullName, m => m.Version, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<ThunderstorePackage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(ThunderstorePackage package)
        {
            if (!seen.Add(package.FullName)) return;
            foreach (var text in package.Latest.Dependencies)
            {
                if (ThunderstoreDependency.Parse(text) is { } dependency && catalog.Find(dependency.FullName) is { } found) Visit(found);
            }

            if (ModSafety.IsLoader(package.FullName) && hasBepInEx && !versions.ContainsKey(package.FullName)) return;
            if (versions.TryGetValue(package.FullName, out var version) && version == package.Latest.VersionNumber) return;
            ordered.Add(package);
        }

        Visit(target);
        if (!hasBepInEx && !ordered.Any(p => ModSafety.IsLoader(p.FullName)) && catalog.Find(ModSafety.BepInExPackFullName) is { } pack)
        {
            ordered.Insert(0, pack);
        }
        return new ModInstallPlan(target, safety, ordered);
    }

    // ----- changing the server folder -----

    /// <summary>
    /// Downloads and installs every package of <paramref name="plan"/>. Refuses (<see cref="InvalidOperationException"/>)
    /// when the plan, or any package in it, is not server-only, checked again here against <paramref name="catalog"/>.
    /// </summary>
    public async Task InstallAsync(string serverFolder, ModInstallPlan plan, ThunderstoreCatalogSnapshot catalog, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!plan.IsAllowed) throw new InvalidOperationException($"{plan.Target.FullName} is not a server-only mod ({plan.Safety.Reason}).");
        foreach (var package in plan.Packages)
        {
            var safety = catalog.Classify(package);
            if (!safety.IsAllowed) throw new InvalidOperationException($"{package.FullName} is not a server-only mod ({safety.Reason}).");
        }

        // Download everything first: a failed download leaves the server as it was.
        var zips = new List<(ThunderstorePackage Package, byte[] Zip)>();
        foreach (var package in plan.Packages)
        {
            progress?.Report(package.FullName);
            zips.Add((package, await _catalog.DownloadAsync(package, cancellationToken)));
        }

        await Task.Run(() =>
        {
            var mods = GetInstalled(serverFolder).ToList();
            foreach (var (package, zip) in zips)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previous = mods.FirstOrDefault(m => string.Equals(m.FullName, package.FullName, StringComparison.OrdinalIgnoreCase));
                if (previous is not null)
                {
                    DeleteFiles(serverFolder, previous);
                    mods.Remove(previous);
                }

                using var archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
                var files = ModSafety.IsLoader(package.FullName)
                    ? ExtractLoader(archive, serverFolder)
                    : ExtractPackage(archive, serverFolder, package.FullName);

                var mod = new InstalledMod
                {
                    FullName = package.FullName,
                    Version = package.Latest.VersionNumber,
                    Enabled = previous?.Enabled ?? true,
                    AsDependency = previous?.AsDependency ?? !string.Equals(package.FullName, plan.Target.FullName, StringComparison.OrdinalIgnoreCase),
                    InstalledAtUtc = DateTime.UtcNow,
                    Dependencies = package.Latest.Dependencies
                        .Select(ThunderstoreDependency.Parse).Where(d => d is not null).Select(d => d!.Value.FullName).ToList(),
                    Files = files,
                };
                if (string.Equals(package.FullName, plan.Target.FullName, StringComparison.OrdinalIgnoreCase)) mod.AsDependency = false;
                if (!mod.Enabled) SetFilesEnabled(serverFolder, mod, enabled: false);
                mods.Add(mod);
                SaveManifest(serverFolder, mods);
                _logger.Information("Installed mod {package} {version} into {folder} ({count} files)", mod.FullName, mod.Version, serverFolder, files.Count);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Removes a mod the app installed, then the dependencies it installed that nothing needs any more. BepInEx
    /// itself stays (other plugins, such as the map's, load through it).
    /// </summary>
    public void Uninstall(string serverFolder, string fullName)
    {
        if (ModSafety.IsLoader(fullName)) throw new InvalidOperationException("BepInEx is not removed by the app.");
        var mods = GetInstalled(serverFolder).ToList();
        var mod = mods.FirstOrDefault(m => string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{fullName} was not installed by the app.");

        DeleteFiles(serverFolder, mod);
        mods.Remove(mod);
        _logger.Information("Removed mod {package} from {folder}", fullName, serverFolder);

        // Dependencies nobody needs any more go too.
        bool removed;
        do
        {
            removed = false;
            var needed = new HashSet<string>(mods.SelectMany(m => m.Dependencies), StringComparer.OrdinalIgnoreCase);
            foreach (var orphan in mods.Where(m => m.AsDependency && !m.IsLoader && !needed.Contains(m.FullName)).ToList())
            {
                DeleteFiles(serverFolder, orphan);
                mods.Remove(orphan);
                removed = true;
                _logger.Information("Removed mod {package}, which no other mod needs", orphan.FullName);
            }
        } while (removed);

        SaveManifest(serverFolder, mods);
    }

    /// <summary>Turns a mod off (its <c>.dll</c> files renamed to <c>.dll.old</c>, which BepInEx skips) or back on.</summary>
    public void SetEnabled(string serverFolder, string fullName, bool enabled)
    {
        if (ModSafety.IsLoader(fullName)) throw new InvalidOperationException("BepInEx cannot be turned off by the app.");
        var mods = GetInstalled(serverFolder).ToList();
        var mod = mods.FirstOrDefault(m => string.Equals(m.FullName, fullName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{fullName} was not installed by the app.");
        if (mod.Enabled == enabled) return;

        SetFilesEnabled(serverFolder, mod, enabled);
        mod.Enabled = enabled;
        SaveManifest(serverFolder, mods);
        _logger.Information("{action} mod {package} in {folder}", enabled ? "Enabled" : "Disabled", fullName, serverFolder);
    }

    // ----- file work -----

    /// <summary>
    /// Lays a mod package out under <c>BepInEx</c> as r2modman does: <c>plugins/</c>, <c>patchers/</c> and
    /// <c>monomod/</c> go into a <c>Owner-Name</c> folder of their own, <c>config/</c> into <c>BepInEx/config</c>
    /// (never over a config the server already has), anything else into the plugin folder. A copy of BepInEx core
    /// inside a package is ignored.
    /// </summary>
    internal static List<string> ExtractPackage(ZipArchive archive, string serverFolder, string fullName)
    {
        var written = new List<string>();
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // a folder
            var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count == 0) continue;
            if (parts.Count == 1 && PackageMetaFiles.Contains(parts[0])) continue;
            if (parts.Count > 1 && parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);

            var top = parts[0].ToLowerInvariant();
            List<string> target;
            var overwrite = true;
            switch (top)
            {
                case "plugins" when parts.Count > 1:
                case "patchers" when parts.Count > 1:
                case "monomod" when parts.Count > 1:
                    target = new List<string> { "BepInEx", top, fullName };
                    target.AddRange(parts.Skip(1));
                    break;
                case "config" when parts.Count > 1:
                    target = new List<string> { "BepInEx", "config" };
                    target.AddRange(parts.Skip(1));
                    overwrite = false;
                    break;
                case "core" when parts.Count > 1:
                    continue;
                default:
                    target = new List<string> { "BepInEx", "plugins", fullName };
                    target.AddRange(parts);
                    break;
            }

            if (WriteEntry(entry, serverFolder, target, overwrite) is { } relative) written.Add(relative);
        }
        return written;
    }

    /// <summary>
    /// BepInExPack_Valheim: the pack's <c>BepInExPack_Valheim/</c> folder is the server folder's layout (BepInEx,
    /// Doorstop, winhttp.dll). Existing configs are kept.
    /// </summary>
    internal static List<string> ExtractLoader(ZipArchive archive, string serverFolder)
    {
        var written = new List<string>();
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (parts.Count < 2 || !parts[0].Equals("BepInExPack_Valheim", StringComparison.OrdinalIgnoreCase)) continue;
            parts.RemoveAt(0);

            var isConfig = parts.Count > 2 && parts[0].Equals("BepInEx", StringComparison.OrdinalIgnoreCase)
                           && parts[1].Equals("config", StringComparison.OrdinalIgnoreCase);
            if (WriteEntry(entry, serverFolder, parts, overwrite: !isConfig) is { } relative) written.Add(relative);
        }
        if (written.Count == 0) throw new InvalidDataException("The BepInExPack download has no BepInExPack_Valheim folder.");
        return written;
    }

    /// <summary>Writes one entry; returns its relative path to record, or null for a config (written only when missing).</summary>
    private static string? WriteEntry(ZipArchiveEntry entry, string serverFolder, List<string> parts, bool overwrite)
    {
        if (parts.Any(p => p is "." or "..")) throw new InvalidDataException($"Unsafe path in the mod package: {entry.FullName}");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serverFolder)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Join(new[] { serverFolder }.Concat(parts).ToArray()));
        if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsafe path in the mod package: {entry.FullName}");
        }

        if (!overwrite && File.Exists(path)) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        entry.ExtractToFile(path, overwrite: true);
        // Configs are the server owner's from now on: never recorded, so an update or a removal keeps them.
        return overwrite ? string.Join('/', parts) : null;
    }

    private static void SetFilesEnabled(string serverFolder, InstalledMod mod, bool enabled)
    {
        for (var i = 0; i < mod.Files.Count; i++)
        {
            var file = mod.Files[i];
            if (enabled && file.EndsWith(".dll" + DisabledSuffix, StringComparison.OrdinalIgnoreCase))
            {
                var on = file[..^DisabledSuffix.Length];
                Move(serverFolder, file, on);
                mod.Files[i] = on;
            }
            else if (!enabled && file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                var off = file + DisabledSuffix;
                Move(serverFolder, file, off);
                mod.Files[i] = off;
            }
        }
    }

    private static void Move(string serverFolder, string from, string to)
    {
        var source = Path.Join(serverFolder, from);
        if (File.Exists(source)) File.Move(source, Path.Join(serverFolder, to), overwrite: true);
    }

    private static void DeleteFiles(string serverFolder, InstalledMod mod)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in mod.Files)
        {
            var path = Path.Join(serverFolder, file);
            if (File.Exists(path)) File.Delete(path);
            if (Path.GetDirectoryName(path) is { } folder) folders.Add(folder);
        }

        // Empty folders left behind go, up to (not including) BepInEx's own folders.
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            Path.GetFullPath(serverFolder),
            Path.GetFullPath(Path.Join(serverFolder, "BepInEx")),
            Path.GetFullPath(PluginsFolder(serverFolder)),
            Path.GetFullPath(Path.Join(serverFolder, "BepInEx", "patchers")),
            Path.GetFullPath(Path.Join(serverFolder, "BepInEx", "config")),
            Path.GetFullPath(Path.Join(serverFolder, "BepInEx", "monomod")),
        };
        foreach (var start in folders.OrderByDescending(f => f.Length))
        {
            var folder = Path.GetFullPath(start);
            while (!keep.Contains(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                folder = Path.GetDirectoryName(folder)!;
            }
        }
    }

    private static void SaveManifest(string serverFolder, List<InstalledMod> mods)
    {
        var path = ManifestPath(serverFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(mods, Formatting.Indented));
        File.Move(temp, path, overwrite: true);
    }
}
