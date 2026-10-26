using ValheimServerGUI.Game.WorldMap;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    /// <summary>
    /// Drawing a world from its seed. The terrain itself is SeedLab's (verified upstream against Valheim 1.0); these
    /// check VSG's use of it: deterministic output, land at the spawn, the polar biomes where the game puts them, and
    /// nothing drawn outside the world.
    /// </summary>
    public class WorldMapRendererTests
    {
        private static readonly WorldMapRenderer Renderer = new(ValheimHash.SeedFromName("j3QV2ftr3y"));

        private static uint[] Render(WorldMapRegion region, int width, int height)
        {
            var pixels = new uint[width * height];
            Renderer.Render(region, width, height, pixels);
            return pixels;
        }

        private static (byte A, byte R, byte G, byte B) Unpack(uint p) => ((byte)(p >> 24), (byte)(p >> 16), (byte)(p >> 8), (byte)p);

        [Fact]
        public void Render_IsDeterministic()
        {
            var region = WorldMapRegion.WholeWorld(64);

            Assert.Equal(Render(region, 64, 64), Render(region, 64, 64));
        }

        [Fact]
        public void Render_OutsideTheWorld_IsTransparent()
        {
            var pixels = Render(WorldMapRegion.WholeWorld(64), 64, 64);

            Assert.Equal(0u, pixels[0]); // the square's corner lies outside the world disc
            Assert.Equal(255, Unpack(pixels[32 * 64 + 32]).A);
        }

        [Fact]
        public void Render_Spawn_IsGreenMeadows()
        {
            // A 20 m square around the centre of the world, where every world starts in the Meadows.
            var (a, r, g, b) = Unpack(Render(new WorldMapRegion(-10, 10, 5), 4, 4)[5]);

            Assert.Equal(255, a);
            Assert.True(g > r && g > b, $"expected green, got ({r},{g},{b})");
        }

        [Fact]
        public void Render_FarSouth_IsAshlandsRed()
        {
            // Ashlands rings the south of every world beyond ~8000 m from the centre.
            var pixels = Render(new WorldMapRegion(-1000, -9000, 25), 80, 40);
            var red = 0;
            foreach (var p in pixels)
            {
                var (_, r, g, b) = Unpack(p);
                if (r > g + 40 && r > b + 40) red++;
            }

            Assert.True(red > pixels.Length / 4, $"only {red} of {pixels.Length} pixels look like Ashlands");
        }
    }
}
