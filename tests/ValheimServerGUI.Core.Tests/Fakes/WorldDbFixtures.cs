using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using ValheimServerGUI.Game.WorldMap;

namespace ValheimServerGUI.Core.Tests.Fakes
{
    /// <summary>
    /// Writes world databases (<c>.db2</c>) the way the game's <c>ZNet.SaveWorldThread</c> and <c>ZoneSystem.Save</c>
    /// do, for the map's save reader.
    /// </summary>
    public static class WorldDbFixtures
    {
        public static byte[] Build(
            IEnumerable<(string Prefab, float X, float Z)> locations,
            IEnumerable<(short X, short Y)>? zones = null,
            int version = 41,
            Action<BinaryWriter>? extraZoneField = null,
            bool withTrailer = true)
        {
            using var block = new MemoryStream();
            using (var writer = new BinaryWriter(block))
            {
                var zoneList = new List<(short X, short Y)>(zones ?? Array.Empty<(short, short)>());
                writer.Write(zoneList.Count);
                foreach (var (x, y) in zoneList)
                {
                    writer.Write(x);
                    writer.Write(y);
                }
                writer.Write(32); // locationVersion
                writer.Write(1);
                writer.Write("defeated_eikthyr");
                writer.Write(true); // locationsGenerated

                var locationList = new List<(string Prefab, float X, float Z)>(locations);
                writer.Write(locationList.Count);
                foreach (var (prefab, x, z) in locationList)
                {
                    writer.Write(ValheimHash.GetStableHashCode(prefab));
                    writer.Write(x);
                    writer.Write(42f); // altitude
                    writer.Write(z);
                    writer.Write(false);
                }
                extraZoneField?.Invoke(writer);
            }

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write(block.ToArray());
            }

            using var file = new MemoryStream();
            using (var writer = new BinaryWriter(file))
            {
                writer.Write(version);
                writer.Write(1234.5); // netTime
                writer.Write((int)compressed.Length);
                writer.Write(compressed.ToArray());
                if (withTrailer)
                {
                    // RandEventSystem + PersistentEventSystem blocks, which the map does not read.
                    writer.Write(0f);
                    writer.Write("");
                    writer.Write(0f);
                    writer.Write(0f);
                    writer.Write(0f);
                    writer.Write(0f);
                    writer.Write(0);
                }
            }
            return file.ToArray();
        }

        /// <summary>Writes <c>_main.&lt;number&gt;.db2</c> next to a save written by <see cref="WorldSaveFixtures.WriteSave"/>.</summary>
        public static void WriteDb(DirectoryInfo worldFolder, ulong number, byte[] db2)
        {
            worldFolder.Create();
            File.WriteAllBytes(Path.Join(worldFolder.FullName, $"_main.{number}.db2"), db2);
        }
    }
}
