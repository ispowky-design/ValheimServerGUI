using System;
using System.IO;
using System.Linq;
using System.Text;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game.WorldMap;
using ValheimServerGUI.Localization;
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
            Assert.Equal("Base", Assert.Single(shared.Pins).Name); // on both tables
        }

        [Fact]
        public void FindSharedMaps_SkipsOtherCompressedDataAndFindsTablesAfterIt()
        {
            var signText = Gzip(Encoding.UTF8.GetBytes("Welcome to the longhouse"));
            var notAMap = Gzip(new byte[] { 3, 0, 0, 0, 7, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1 }); // 7 pixels is not a square
            var truncated = Gzip(new byte[] { 3, 0, 0, 0, 0, 0, 64, 0 }).ToArray(); // declares 2048² pixels, holds none
            var chunk = BuildChunk(new[] { new[] { Pixel(100, 100) } }, signText, notAMap, truncated);

            var maps = SharedMapReader.FindSharedMaps(chunk);

            Assert.True(Assert.Single(maps).Exploration.IsExplored(100, 100));
        }

        [Fact]
        public void ReadFromWorldFolder_ReadsEveryPinOnce()
        {
            var explored = new[] { Pixel(0, 0) };
            WriteChunk("20_20__0_3.chunk", BuildChunkOf(new[]
            {
                SharedMap(explored, new[]
                {
                    new TablePin("HOME", 120.5f, -40.25f, Type: 1),
                    new TablePin("Crypte", 800, 900, Type: 6),
                    new TablePin("Feu de camp", -10, 15, Type: 0),
                    new TablePin("$enemy_eikthyr", 300, 400, Type: 9),
                    new TablePin("Mort", 5, 5, Type: 4), // deaths are never shared; left out if met
                }),
            }));
            WriteChunk("1e_20__1_1.chunk", BuildChunkOf(new[]
            {
                SharedMap(explored, new[]
                {
                    new TablePin("HOME", 120.6f, -40.3f, Type: 1), // the same pin, copied by a player
                    new TablePin("Crypte", 800, 900, Type: 6, Checked: true),
                    new TablePin("$hud_pin_hildir1", -500, 600, Type: 14),
                    new TablePin("Marteau", 50, 60, Type: 2),
                    new TablePin("New", 70, 80, Type: 42), // a type from a later game version
                }),
            }));

            var pins = SharedMapReader.ReadFromWorldFolder(_folder)!.Pins;

            Assert.All(pins, p => Assert.Equal(WorldLocationKind.Pin, p.Kind));
            Assert.Equal(7, pins.Count);
            var home = Assert.Single(pins, p => p.Name == "HOME");
            Assert.Equal((WorldMapPinIcon.Home, false), (home.PinIcon, home.IsChecked));
            Assert.Equal(120.5f, home.X, 0.2f);
            Assert.Equal(-40.25f, home.Z, 0.2f);
            Assert.Equal((WorldMapPinIcon.Rune, true), Pin("Crypte"));
            Assert.Equal((WorldMapPinIcon.Fire, false), Pin("Feu de camp"));
            Assert.Equal((WorldMapPinIcon.Hammer, false), Pin("Marteau"));
            Assert.Equal((WorldMapPinIcon.Dot, false), Pin("New"));
            Assert.Equal((WorldMapPinIcon.Boss, false), Pin(Strings.MapLocation_Eikthyr));
            Assert.Equal((WorldMapPinIcon.Quest, false), Pin("")); // a game text this app cannot name
            Assert.DoesNotContain(pins, p => p.Name == "Mort");

            (WorldMapPinIcon, bool) Pin(string name)
            {
                var pin = Assert.Single(pins, p => p.Name == name);
                return (pin.PinIcon, pin.IsChecked);
            }
        }

        [Fact]
        public void FindSharedMaps_ReadsThePinsOfAnOlderTableWithoutAuthors()
        {
            var chunk = BuildChunkOf(new[] { SharedMap(new[] { Pixel(10, 10) }, new[] { new TablePin("Ferme", 10, 10, Type: 1) }, version: 2) });

            var pin = Assert.Single(Assert.Single(SharedMapReader.FindSharedMaps(chunk)).Pins);

            Assert.Equal(("Ferme", WorldMapPinIcon.Home), (pin.Name, pin.PinIcon));
        }

        [Fact]
        public void FindSharedMaps_KeepsTheDiscoveredAreaWhenThePinsDoNotRead()
        {
            var pixels = new byte[MapSize * MapSize];
            var (col, row) = Pixel(100, 100);
            pixels[row * MapSize + col] = 1;
            byte[] Table(params byte[] pins) => Gzip(new byte[] { 3, 0, 0, 0, 0, 0, 64, 0 }.Concat(pixels).Concat(pins).ToArray());

            var maps = SharedMapReader.FindSharedMaps(BuildChunkOf(new[]
            {
                Table(),                                       // no pin count at all
                Table(2, 0, 0, 0, 1, 2, 3),                    // cut off inside the first pin
                Table(1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x80, 0x80, 0x04),  // a 64 KiB name
                Table(0xFF, 0xFF, 0xFF, 0xFF),                 // a negative pin count
            }));

            Assert.Equal(4, maps.Count);
            Assert.All(maps, map =>
            {
                Assert.True(map.Exploration.IsExplored(100, 100));
                Assert.Empty(map.Pins);
            });
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
