using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValheimServerGUI.Game.Mods;

namespace ValheimServerGUI.Core.Tests.Game.Mods;

/// <summary>Builds Thunderstore packages, package zips and a catalog that serves them without the network.</summary>
internal static class ModTestPackages
{
    public static ThunderstorePackage Package(
        string fullName,
        string version = "1.0.0",
        string[]? categories = null,
        string description = "A mod.",
        string[]? dependencies = null,
        bool deprecated = false)
    {
        var dash = fullName.IndexOf('-');
        return new ThunderstorePackage(
            Owner: fullName[..dash],
            Name: fullName[(dash + 1)..],
            Categories: categories ?? new[] { "Server-side" },
            IsDeprecated: deprecated,
            HasNsfwContent: false,
            PackageUrl: $"https://thunderstore.io/c/valheim/p/{fullName.Replace('-', '/')}/",
            DateUpdated: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TotalDownloads: 100,
            RatingScore: 5,
            Latest: new ThunderstoreVersion(version, description, dependencies ?? Array.Empty<string>(),
                $"https://thunderstore.io/package/download/{fullName.Replace('-', '/')}/{version}/", 1000, null, null));
    }

    public static ThunderstorePackage BepInExPack(string version = "5.4.2202")
        => Package(ModSafety.BepInExPackFullName, version, new[] { "Libraries" }, "BepInEx pack for Valheim.");

    public static ThunderstoreCatalogSnapshot Catalog(params ThunderstorePackage[] packages)
        => new(packages, DateTime.UtcNow, isStale: false);

    public static byte[] Zip(params (string Path, string Content)[] entries)
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

    public static byte[] BepInExPackZip() => Zip(
        ("manifest.json", "{}"),
        ("icon.png", "png"),
        ("BepInExPack_Valheim/winhttp.dll", "doorstop"),
        ("BepInExPack_Valheim/doorstop_config.ini", "[General]"),
        ("BepInExPack_Valheim/BepInEx/core/BepInEx.dll", "bepinex"),
        ("BepInExPack_Valheim/BepInEx/config/BepInEx.cfg", "pack config"));
}

/// <summary>A catalog whose downloads come from a dictionary of zips.</summary>
internal sealed class FakeThunderstoreCatalog : IThunderstoreCatalog
{
    public ThunderstoreCatalogSnapshot Snapshot { get; set; } = ModTestPackages.Catalog();

    public Dictionary<string, byte[]> Zips { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Downloads { get; } = new();

    public Exception? FailWith { get; set; }

    public Task<ThunderstoreCatalogSnapshot> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
        => FailWith is not null ? Task.FromException<ThunderstoreCatalogSnapshot>(FailWith) : Task.FromResult(Snapshot);

    public Task<byte[]> DownloadAsync(ThunderstorePackage package, CancellationToken cancellationToken)
    {
        Downloads.Add(package.FullName);
        return Zips.TryGetValue(package.FullName, out var zip)
            ? Task.FromResult(zip)
            : Task.FromException<byte[]>(new IOException("no zip for " + package.FullName));
    }
}
