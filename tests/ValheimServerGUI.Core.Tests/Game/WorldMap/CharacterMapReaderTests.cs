using System;
using System.IO;
using System.Linq;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game;
using ValheimServerGUI.Game.WorldMap;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    /// <summary>Reading a character's discovered map of a world, and the discovered area the map uses.</summary>
    public class CharacterMapReaderTests
    {
        private const long WorldUid = 987654321L;

        private static CharacterWorldMap? Read(byte[] bytes, long uid = WorldUid)
            => CharacterMapReader.Read(new MemoryStream(bytes), uid);

        // Minimap pixel for a world point: round(x / 12 + 1024), the row growing northwards.
        private static (int Col, int Row) Pixel(float x, float z) => ((int)Math.Round(x / 12 + 1024), (int)Math.Round(z / 12 + 1024));

        [Fact]
        public void Read_ReturnsTheCharactersMapOfTheWorld()
        {
            var bytes = CharacterSaveFixtures.Build("Ragnar", new[]
            {
                (111L, Enumerable.Empty<(int, int)>()),
                (WorldUid, new[] { Pixel(0, 0), Pixel(600, 1200) }.AsEnumerable()),
            });

            var map = Read(bytes);

            Assert.NotNull(map);
            Assert.Equal("Ragnar", map.CharacterName);
            Assert.True(map.Exploration.IsExplored(0, 0));
            Assert.True(map.Exploration.IsExplored(603, 1197));
            Assert.False(map.Exploration.IsExplored(600, -1200)); // north is not mirrored to the south
            Assert.False(map.Exploration.IsExplored(1200, 600));
        }

        [Fact]
        public void Read_CharacterNeverInTheWorld_ReturnsNull()
        {
            var bytes = CharacterSaveFixtures.Build("Ragnar", new[] { (111L, new[] { Pixel(0, 0) }.AsEnumerable()) });

            Assert.Null(Read(bytes));
        }

        [Fact]
        public void Read_Truncated_Throws()
        {
            var bytes = CharacterSaveFixtures.Build("Ragnar", new[] { (WorldUid, new[] { Pixel(0, 0) }.AsEnumerable()) });

            Assert.Throws<InvalidDataException>(() => Read(bytes.Take(bytes.Length / 2).ToArray()));
        }

        [Fact]
        public void FindCharacterFiles_ListsSavesInBothFolders()
        {
            var root = new DirectoryInfo(Path.Join(Path.GetTempPath(), "vsg_chars_" + Guid.NewGuid().ToString("N")));
            try
            {
                Directory.CreateDirectory(Path.Join(root.FullName, "characters_local"));
                Directory.CreateDirectory(Path.Join(root.FullName, "characters"));
                File.WriteAllBytes(Path.Join(root.FullName, "characters_local", "b.fch"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Join(root.FullName, "characters_local", "b.fch.old"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Join(root.FullName, "characters", "a.fch"), Array.Empty<byte>());

                Assert.Equal(new[] { "b.fch", "a.fch" }, CharacterMapReader.FindCharacterFiles(root).Select(f => f.Name));
            }
            finally
            {
                root.Delete(true);
            }
        }

        [Fact]
        public void GeneratedZones_CoverTheirSixtyFourMetreSquare()
        {
            var exploration = WorldMapExploration.FromGeneratedZones(new (short, short)[] { (0, 0), (2, -1) });

            Assert.True(exploration.IsExplored(31, -31));
            Assert.False(exploration.IsExplored(33, 0));
            Assert.True(exploration.IsExplored(128, -64));
            Assert.False(exploration.IsExplored(128, 64));
        }

        [Fact]
        public void Read_RealSave_ReturnsTheWorldUid()
        {
            var metadata = WorldMetadataReader.Read(new MemoryStream(WorldSaveFixtures.ReadFixture(WorldSaveFixtures.WithGlobalKey)));

            Assert.NotNull(metadata.Uid);
            Assert.NotEqual(0L, metadata.Uid);
        }
    }
}
