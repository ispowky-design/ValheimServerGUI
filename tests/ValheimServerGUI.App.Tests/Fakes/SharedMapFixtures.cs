using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ValheimServerGUI.App.Tests.Fakes
{
    /// <summary>
    /// Writes world chunk files (<c>*.chunk</c>) holding cartography tables, the way <c>ZDOMan.SaveChunk</c> and
    /// <c>ZDO.Save</c> lay out ZDOs, with the table's map stored as <c>MapTable.OnWrite</c> does.
    /// </summary>
    public static class SharedMapFixtures
    {
        public const int MapSize = 2048;

        private static readonly int DataKey = ValheimServerGUI.Game.WorldMap.ValheimHash.GetStableHashCode("data");

        /// <summary>The minimap pixel (col, row from the south) that holds a world point.</summary>
        public static (int Col, int Row) Pixel(float x, float z) => ((int)System.Math.Round(x / 12 + 1024), (int)System.Math.Round(z / 12 + 1024));

        /// <param name="tables">Per cartography table: the minimap pixels shared on it.</param>
        /// <param name="otherBlobs">Byte arrays of other objects (for example a sign's gzip-compressed text).</param>
        public static byte[] BuildChunk(IEnumerable<IEnumerable<(int Col, int Row)>> tables, params byte[][] otherBlobs)
        {
            var zdos = tables.Select(BuildSharedMap).Concat(otherBlobs).ToList();
            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file))
            {
                w.Write((short)41);
                w.Write(zdos.Count);
                foreach (var blob in zdos)
                {
                    w.Write((ushort)(128 | 256)); // byte arrays, persistent
                    w.Write((short)1); w.Write((short)2); // sector
                    w.Write(100f); w.Write(30f); w.Write(200f); // position
                    w.Write(123456); // prefab
                    w.Write((byte)1);
                    w.Write(DataKey);
                    w.Write(blob.Length);
                    w.Write(blob);
                }
            }
            return file.ToArray();
        }

        public static byte[] Gzip(byte[] data)
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(data);
            return compressed.ToArray();
        }

        private static byte[] BuildSharedMap(IEnumerable<(int Col, int Row)> explored)
        {
            var pixels = new byte[MapSize * MapSize];
            foreach (var (col, row) in explored) pixels[row * MapSize + col] = 1;

            using var package = new MemoryStream();
            using (var w = new BinaryWriter(package, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(3);
                w.Write(pixels.Length);
                w.Write(pixels);
                w.Write(1); // pins
                w.Write(76561198000000000L);
                w.Write("Base");
                w.Write(0f); w.Write(0f); w.Write(0f);
                w.Write(3);
                w.Write(false);
                w.Write("Steam_76561198000000000");
            }
            return Gzip(package.ToArray());
        }
    }
}
