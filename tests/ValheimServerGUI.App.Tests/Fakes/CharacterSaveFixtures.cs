using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace ValheimServerGUI.App.Tests.Fakes
{
    /// <summary>
    /// Writes character saves (<c>.fch</c>) the way the game's <c>PlayerProfile.SavePlayerToDisk</c> does (version 46),
    /// with one world map per entry.
    /// </summary>
    public static class CharacterSaveFixtures
    {
        public const int MapSize = 2048;

        /// <param name="worlds">Per world: its uid and the minimap pixels (col, row) the character explored.</param>
        public static byte[] Build(string name, IEnumerable<(long Uid, IEnumerable<(int Col, int Row)> Explored)> worlds)
        {
            using var package = new MemoryStream();
            using (var w = new BinaryWriter(package, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(46);
                w.Write(3);  // stat count
                w.Write(2);  // categories
                for (var c = 0; c < 2; c++)
                {
                    for (var i = 0; i < 3; i++) w.Write(1f);
                    WriteList(w, ("TestWorld", 1f));
                    WriteList(w);
                    WriteList(w);
                    w.Write(5);
                    for (var k = 0; k < 5; k++) WriteList(w, ("$enemy_boar", 2f));
                    for (var k = 0; k < 5; k++) WriteList(w);
                }
                w.Write(false); // firstSpawn

                var list = new List<(long Uid, IEnumerable<(int Col, int Row)> Explored)>(worlds);
                w.Write(list.Count);
                foreach (var (uid, explored) in list)
                {
                    w.Write(uid);
                    for (var v = 0; v < 3; v++)
                    {
                        w.Write(true);
                        w.Write(1f); w.Write(2f); w.Write(3f);
                    }
                    w.Write(0f); w.Write(0f); w.Write(0f); // home
                    w.Write(true);
                    var map = BuildMap(explored);
                    w.Write(map.Length);
                    w.Write(map);
                }

                w.Write(name);
                w.Write(123456789L);
                w.Write("");
                w.Write(false);
                w.Write(1700000000L);
                w.Write(false);
            }

            using var file = new MemoryStream();
            using (var w = new BinaryWriter(file))
            {
                w.Write((int)package.Length);
                w.Write(package.ToArray());
                w.Write(64);
                w.Write(new byte[64]);
            }
            return file.ToArray();
        }

        private static void WriteList(BinaryWriter w, params (string Key, float Value)[] items)
        {
            w.Write(items.Length);
            foreach (var (key, value) in items)
            {
                w.Write(key);
                w.Write(value);
            }
        }

        private static byte[] BuildMap(IEnumerable<(int Col, int Row)> explored)
        {
            var mine = new byte[MapSize * MapSize];
            foreach (var (col, row) in explored) mine[row * MapSize + col] = 1;

            using var inner = new MemoryStream();
            using (var w = new BinaryWriter(inner, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                w.Write(MapSize);
                w.Write(mine);
                w.Write(new byte[MapSize * MapSize]); // shared by others
                w.Write(1);
                w.Write("$enemy_eikthyr");
                w.Write(0f); w.Write(0f); w.Write(0f);
                w.Write(9);
                w.Write(false);
                w.Write(0L);
                w.Write("");
                w.Write(false);
            }

            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(inner.ToArray());

            using var blob = new MemoryStream();
            using (var w = new BinaryWriter(blob))
            {
                w.Write(8); // map version
                w.Write((int)compressed.Length);
                w.Write(compressed.ToArray());
            }
            return blob.ToArray();
        }
    }
}
