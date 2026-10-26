using System;
using System.IO;
using System.Linq;
using System.Text;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game.WorldMap;
using Xunit;
using static ValheimServerGUI.Core.Tests.Fakes.SharedMapFixtures;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    /// <summary>Reading what a world's cartography tables hold from its chunk files.</summary>
    public sealed class SharedMapReaderTests : IDisposable
    {
        private readonly DirectoryInfo _folder =
            Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "vsg_sharedmap_" + Guid.NewGuid().ToString("N")));

        public void Dispose() => _folder.Delete(true);

        private void WriteChunk(string name, byte[] bytes) => File.WriteAllBytes(Path.Join(_folder.FullName, name), bytes);

        [Fact]
        public void ReadFromWorldFolder_CombinesEveryTable()
        {
            WriteChunk("20_20__0_3.chunk", BuildChunk(new[] { new[] { Pixel(0, 0), Pixel(600, 1200) } }));
            WriteChunk("1e_20__1_1.chunk", BuildChunk(new[] { new[] { Pixel(-3000, -2000) } }));

            var shared = SharedMapReader.ReadFromWorldFolder(_folder);

            Assert.NotNull(shared);
            Assert.Equal(2, shared.TableCount);
            Assert.True(shared.Exploration.IsExplored(0, 0));
            Assert.True(shared.Exploration.IsExplored(603, 1197));
            Assert.True(shared.Exploration.IsExplored(-3000, -2000));
            Assert.False(shared.Exploration.IsExplored(600, -1200)); // north is not mirrored to the south
            Assert.False(shared.Exploration.IsExplored(2000, 2000));
        }

        [Fact]
        public void FindSharedMaps_SkipsOtherCompressedDataAndFindsTablesAfterIt()
        {
            var signText = Gzip(Encoding.UTF8.GetBytes("Welcome to the longhouse"));
            var notAMap = Gzip(new byte[] { 3, 0, 0, 0, 7, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1 }); // 7 pixels is not a square
            var truncated = Gzip(new byte[] { 3, 0, 0, 0, 0, 0, 64, 0 }).ToArray(); // declares 2048² pixels, holds none
            var chunk = BuildChunk(new[] { new[] { Pixel(100, 100) } }, signText, notAMap, truncated);

            var maps = SharedMapReader.FindSharedMaps(chunk);

            Assert.True(Assert.Single(maps).IsExplored(100, 100));
        }

        [Fact]
        public void ReadFromWorldFolder_ReturnsNullWithoutTables()
        {
            Assert.Null(SharedMapReader.ReadFromWorldFolder(new DirectoryInfo(Path.Join(_folder.FullName, "missing"))));

            WriteChunk("20_20__0_1.chunk", BuildChunk(Array.Empty<(int, int)[]>(), Gzip(new byte[64])));
            WriteChunk("garbage.chunk", new byte[] { 0x1F, 0x8B, 0x08, 0, 0 });
            Assert.Null(SharedMapReader.ReadFromWorldFolder(_folder));
        }

        [Fact]
        public void Combine_KeepsEveryExploredCellOfMapsOnTheSameGrid()
        {
            var a = WorldMapExploration.FromMinimap(4, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
            var b = WorldMapExploration.FromMinimap(4, new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1 });
            var zones = WorldMapExploration.FromGeneratedZones(new (short, short)[] { (10, 10) });

            var all = WorldMapExploration.Combine(new[] { a, b, zones });

            Assert.NotNull(all);
            Assert.Equal(2, all.Explored.Count(e => e != 0));
            Assert.Null(WorldMapExploration.Combine(Array.Empty<WorldMapExploration>()));
        }
    }
}
