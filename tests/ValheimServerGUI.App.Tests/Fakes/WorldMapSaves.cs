using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ValheimServerGUI.Game.WorldMap;

namespace ValheimServerGUI.App.Tests.Fakes;

/// <summary>
/// Writes a committed 1.0 world save (<c>.fwl2</c> + <c>.db2</c> + <c>.ok</c>) with the given seed and locations, laid
/// out the way the game writes them, for the World Map tab.
/// </summary>
public static class WorldMapSaves
{
    public static void Write(DirectoryInfo worldFolder, string seedName, IEnumerable<(string Prefab, float X, float Z)> locations,
        IEnumerable<(short X, short Y)>? zones = null)
    {
        worldFolder.Create();
        var path = Path.Join(worldFolder.FullName, "_main.1");

        using (var payload = new MemoryStream())
        {
            using (var w = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(41);
                w.Write(worldFolder.Name);
                w.Write(seedName);
                w.Write(ValheimHash.SeedFromName(seedName));
                w.Write(1L);
                w.Write(2); // worldGenVersion
                w.Write(true);
                w.Write(0); // global keys
                w.Write(0); // player history
            }
            using var file = new BinaryWriter(File.Create(path + ".fwl2"));
            file.Write((int)payload.Length);
            file.Write(payload.ToArray());
        }

        using (var block = new MemoryStream())
        {
            using (var w = new BinaryWriter(block, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                var zoneList = new List<(short X, short Y)>(zones ?? Array.Empty<(short, short)>());
                w.Write(zoneList.Count);
                foreach (var (x, y) in zoneList)
                {
                    w.Write(x);
                    w.Write(y);
                }
                w.Write(32);
                w.Write(0);
                w.Write(true);
                var list = new List<(string Prefab, float X, float Z)>(locations);
                w.Write(list.Count);
                foreach (var (prefab, x, z) in list)
                {
                    w.Write(ValheimHash.GetStableHashCode(prefab));
                    w.Write(x);
                    w.Write(40f);
                    w.Write(z);
                    w.Write(false);
                }
            }

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(block.ToArray());

            using var file = new BinaryWriter(File.Create(path + ".db2"));
            file.Write(41);
            file.Write(10.0);
            file.Write((int)compressed.Length);
            file.Write(compressed.ToArray());
        }

        File.WriteAllBytes(path + ".ok", BitConverter.GetBytes(41));
    }
}
