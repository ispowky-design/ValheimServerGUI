using System;
using System.IO;
using System.Linq;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game.WorldMap;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    /// <summary>Reading locations and generated zones out of a world's <c>.db2</c> for the map.</summary>
    public class WorldMapSaveReaderTests : IDisposable
    {
        private readonly DirectoryInfo WorldFolder =
            new(Path.Join(Path.GetTempPath(), "vsg_worldmap_" + Guid.NewGuid().ToString("N")));

        public void Dispose()
        {
            if (WorldFolder.Exists) WorldFolder.Delete(recursive: true);
        }

        private static WorldMapSaveData Read(byte[] bytes) => WorldMapSaveReader.Read(new MemoryStream(bytes));

        [Fact]
        public void Read_ReturnsLocationsAndZones()
        {
            var data = Read(WorldDbFixtures.Build(
                new[] { ("StartTemple", 10f, -20f), ("Eikthyrnir", 300.5f, 412.25f), ("WoodHouse1", 1f, 2f) },
                new (short, short)[] { (0, 0), (-1, 3) }));

            Assert.Equal(new (short, short)[] { (0, 0), (-1, 3) }, data.GeneratedZones);
            Assert.Equal(3, data.Locations.Count);
            Assert.Equal(new WorldSaveLocation(ValheimHash.GetStableHashCode("Eikthyrnir"), 300.5f, 412.25f, false), data.Locations[1]);
        }

        [Fact]
        public void Markers_KeepOnlyKnownLocations()
        {
            var data = Read(WorldDbFixtures.Build(
                new[] { ("StartTemple", 10f, -20f), ("Eikthyrnir", 300f, 400f), ("WoodHouse1", 1f, 2f), ("Vendor_BlackForest", -5f, 6f) }));

            var markers = WorldMapMarkers.FromSave(data);

            Assert.Equal(new[] { WorldLocationKind.Spawn, WorldLocationKind.Boss, WorldLocationKind.Trader }, markers.Select(m => m.Kind));
            Assert.Equal(300f, markers[1].X);
            Assert.Equal(400f, markers[1].Z);
        }

        [Fact]
        public void Read_PreOneZeroVersion_Throws()
        {
            Assert.Throws<InvalidDataException>(() => Read(WorldDbFixtures.Build(Array.Empty<(string, float, float)>(), version: 35)));
        }

        [Fact]
        public void Read_UnknownExtraField_Throws()
        {
            var bytes = WorldDbFixtures.Build(new[] { ("StartTemple", 0f, 0f) }, extraZoneField: w => w.Write(7));

            Assert.Throws<InvalidDataException>(() => Read(bytes));
        }

        [Fact]
        public void Read_Truncated_Throws()
        {
            var bytes = WorldDbFixtures.Build(new[] { ("StartTemple", 0f, 0f) }, withTrailer: false);

            Assert.Throws<InvalidDataException>(() => Read(bytes.Take(bytes.Length - 5).ToArray()));
        }

        [Fact]
        public void Read_HugeCount_ThrowsWithoutAllocating()
        {
            var bytes = WorldDbFixtures.Build(Array.Empty<(string, float, float)>());
            // Corrupt the declared compressed length.
            BitConverter.GetBytes(int.MaxValue).CopyTo(bytes, 12);

            Assert.Throws<InvalidDataException>(() => Read(bytes));
        }

        [Fact]
        public void ReadFromWorldFolder_ReadsTheDatabaseOfTheLatestCompleteSave()
        {
            var fwl2 = WorldSaveFixtures.ReadFixture(WorldSaveFixtures.WithGlobalKey);
            WorldSaveFixtures.WriteSave(WorldFolder, 1, fwl2);
            WorldDbFixtures.WriteDb(WorldFolder, 1, WorldDbFixtures.Build(new[] { ("StartTemple", 1f, 1f) }));
            WorldSaveFixtures.WriteSave(WorldFolder, 2, fwl2);
            WorldDbFixtures.WriteDb(WorldFolder, 2, WorldDbFixtures.Build(new[] { ("StartTemple", 2f, 2f), ("GDKing", 3f, 3f) }));
            // Newer but not committed: ignored.
            WorldSaveFixtures.WriteSave(WorldFolder, 3, fwl2, committed: false);
            WorldDbFixtures.WriteDb(WorldFolder, 3, WorldDbFixtures.Build(Array.Empty<(string, float, float)>()));

            var data = WorldMapSaveReader.ReadFromWorldFolder(WorldFolder);

            Assert.NotNull(data);
            Assert.Equal(2, data.Locations.Count);
        }

        [Fact]
        public void ReadFromWorldFolder_NoSave_ReturnsNull()
        {
            Assert.Null(WorldMapSaveReader.ReadFromWorldFolder(WorldFolder));

            WorldSaveFixtures.WriteSave(WorldFolder, 1, WorldSaveFixtures.ReadFixture(WorldSaveFixtures.WithGlobalKey));
            Assert.Null(WorldMapSaveReader.ReadFromWorldFolder(WorldFolder));
        }

        [Fact]
        public void Catalog_EveryTypeHasADisplayNameAndAHash()
        {
            foreach (var type in WorldLocationCatalog.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(type.DisplayName()));
                Assert.Same(type, WorldLocationCatalog.Find(ValheimHash.GetStableHashCode(type.PrefabName)));
            }
        }
    }
}
