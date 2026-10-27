using SeedLab.WorldGen;
using ValheimServerGUI.Game.WorldMap;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    /// <summary>
    /// Drawing a world from its seed. The terrain itself is SeedLab's (verified upstream against Valheim 1.0); these
    /// check VSG's use of it: deterministic output, land at the spawn, the polar biomes where the game puts them,
    /// forests where the in-game map draws them, and nothing drawn outside the world.
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
    
        [Fact]
        public void Forest_IsWhereTheInGameMapDrawsIt()
        {
            Assert.Equal(1f, WorldMapPalette.Forest(Biome.BlackForest, 123, 456));
            Assert.Equal(0f, WorldMapPalette.Forest(Biome.Mountain, 123, 456));
            Assert.Equal(0f, WorldMapPalette.Forest(Biome.Ocean, 123, 456));

            // The Meadows are wooded where WorldGenerator.InForest says so, and open elsewhere.
            int wooded = 0, open = 0;
            for (var x = -2000f; x < 2000; x += 37)
            {
                var forest = WorldMapPalette.Forest(Biome.Meadows, x, 250);
                var factor = WorldGeneratorPort.GetForestFactor(x, 0, 250);
                if (factor < 1.1f) { Assert.Equal(1f, forest); wooded++; }
                if (factor > 1.2f) { Assert.Equal(0f, forest); open++; }
            }
            Assert.True(wooded > 5 && open > 5, $"{wooded} wooded and {open} open samples");
        }

        [Fact]
        public void Shade_DarkensWoodsAndLightsLava()
        {
            for (var x = 0; x < 40; x += 3)
            {
                var open = Unpack(WorldMapPalette.Shade(Biome.Meadows, 50, 50, 50, 1, x, 7, 0, cover: 0));
                var woods = Unpack(WorldMapPalette.Shade(Biome.Meadows, 50, 50, 50, 1, x, 7, 0, cover: 1));
                Assert.True(woods.R + woods.G + woods.B < open.R + open.G + open.B - 60, $"woods {woods} not darker than {open}");
            }

            var rock = Unpack(WorldMapPalette.Shade(Biome.AshLands, 50, 50, 50, 1, 0, 0, 0, cover: 0));
            var lava = Unpack(WorldMapPalette.Shade(Biome.AshLands, 50, 50, 50, 1, 0, 0, 0, cover: 1));
            Assert.True(lava.R > rock.R && lava.G > rock.G + 40, $"lava {lava} does not glow over {rock}");
        }

        [Fact]
        public void Fog_CoversWhatNobodyDiscovered_BrownerAtTheEdge()
        {
            var explored = new byte[64 * 64];
            for (var row = 20; row < 30; row++)
            {
                for (var col = 20; col < 30; col++) explored[row * 64 + col] = 1;
            }
            var fog = new WorldMapFog(new WorldMapExploration(64, 10, 0, 640, explored)).CellPixels();

            Assert.Equal(0u, fog[25 * 64 + 25]);
            var edge = Unpack(fog[25 * 64 + 31]);
            var far = Unpack(fog[60 * 64 + 60]);
            Assert.Equal(255, edge.A);
            Assert.Equal(255, far.A);
            Assert.True(edge.R + edge.G + edge.B < far.R + far.G + far.B - 30, $"edge {edge} is not browner than {far}");
            Assert.InRange(far.R, 190, 240);
        }
    }
}
