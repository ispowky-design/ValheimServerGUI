using System.IO;
using ValheimServerGUI.Core.Tests.Fakes;
using ValheimServerGUI.Game;
using ValheimServerGUI.Game.WorldMap;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    public class ValheimHashTests
    {
        // The real saves store the seed name and the integer the game derived from it: independent ground truth.
        [Theory]
        [InlineData(WorldSaveFixtures.WithGlobalKey)]
        [InlineData(WorldSaveFixtures.TwoPlatforms)]
        public void SeedFromName_MatchesTheSeedTheGameStored(string fixture)
        {
            var metadata = WorldMetadataReader.Read(new MemoryStream(WorldSaveFixtures.ReadFixture(fixture)));

            Assert.NotNull(metadata.Seed);
            Assert.Equal(metadata.Seed, ValheimHash.SeedFromName(metadata.SeedName!));
        }

        [Theory]
        [InlineData("Kh0zDpuPnw", 810132289)]
        [InlineData("q6GhJN6FwT", 517038747)]
        [InlineData("a", 372029373)]
        [InlineData("", 0)]
        public void SeedFromName_KnownSeeds(string name, int seed)
        {
            Assert.Equal(seed, ValheimHash.SeedFromName(name));
        }

        [Fact]
        public void Read_RealSave_ReturnsTheWorldGenVersion()
        {
            var metadata = WorldMetadataReader.Read(new MemoryStream(WorldSaveFixtures.ReadFixture(WorldSaveFixtures.WithGlobalKey)));

            Assert.Equal(2, metadata.WorldGenVersion);
        }
    }
}
