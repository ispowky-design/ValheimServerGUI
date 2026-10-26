using System;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.WorldGen;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>
    /// A rectangle of the world to draw: its top-left corner in world coordinates (X east, Z north) and how many metres
    /// one pixel covers. Pixel rows run north to south, so row 0 is the region's northern edge.
    /// </summary>
    public readonly record struct WorldMapRegion(double Left, double Top, double MetersPerPixel)
    {
        /// <summary>A square region that shows the whole world (and its edge ocean) at the given pixel size.</summary>
        public static WorldMapRegion WholeWorld(int pixels)
        {
            var span = 2 * WorldMapRenderer.MapRadius;
            return new WorldMapRegion(-WorldMapRenderer.MapRadius, WorldMapRenderer.MapRadius, span / pixels);
        }
    }

    /// <summary>
    /// Draws a Valheim world from its seed: biome colours like the in-game map, a hill-shade from the terrain heights,
    /// rivers and lakes, and ocean darkening with depth. The terrain comes from SeedLab's exact port of the game's
    /// world generator (vendored under <c>src/ThirdParty</c>), so the map matches the game for Valheim 1.0 worlds.
    ///
    /// <para>Building a renderer runs the world's pregeneration (lakes, rivers, streams), which takes a moment; do it
    /// off the UI thread. After that, <see cref="Render"/> may be called from several threads at once.</para>
    /// </summary>
    public sealed class WorldMapRenderer
    {
        /// <summary>Distance from the world centre where the playable world ends (the edge of the world ocean).</summary>
        public const float WorldRadius = 10500f;

        /// <summary>Half the side of the square the map shows: the world plus a margin.</summary>
        public const float MapRadius = 10600f;

        /// <summary>Sea level, in world metres.</summary>
        public const float WaterLevel = 30f;

        private readonly WorldGeneratorPort _generator;
        private readonly object _forkLock = new();

        public WorldMapRenderer(int seed, int worldGenVersion = 2)
        {
            Seed = seed;
            _generator = new WorldGeneratorPort(seed, worldGenVersion);
        }

        public int Seed { get; }

        /// <summary>
        /// Renders <paramref name="width"/> × <paramref name="height"/> pixels of <paramref name="region"/> into
        /// <paramref name="pixels"/> as premultiplied BGRA (one <c>uint</c> per pixel, row-major). Outside the world
        /// disc the pixels are transparent.
        /// </summary>
        public void Render(WorldMapRegion region, int width, int height, uint[] pixels, CancellationToken cancellationToken = default)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (pixels.Length < width * height) throw new ArgumentException("The pixel buffer is too small.", nameof(pixels));

            // One extra column and row of heights to the south-east, for the hill-shade's slope.
            var stride = width + 1;
            var heights = new float[stride * (height + 1)];
            var biomes = new Biome[width * height];
            var mpp = region.MetersPerPixel;

            var options = new ParallelOptions { CancellationToken = cancellationToken };
            Parallel.For(0, height + 1, options, ForkGenerator, (row, loop, generator) =>
            {
                var z = (float)(region.Top - (row + 0.5) * mpp);
                for (var col = 0; col <= width; col++)
                {
                    var x = (float)(region.Left + (col + 0.5) * mpp);
                    if (x * x + z * z > MapRadius * MapRadius * 1.02f)
                    {
                        heights[row * stride + col] = -400f;
                        if (row < height && col < width) biomes[row * width + col] = Biome.None;
                        continue;
                    }

                    var biome = generator.GetBiome(x, z);
                    heights[row * stride + col] = generator.GetBiomeHeight(biome, x, z, out _);
                    if (row < height && col < width) biomes[row * width + col] = biome;
                }
                return generator;
            }, _ => { });

            Parallel.For(0, height, options, row =>
            {
                var z = region.Top - (row + 0.5) * mpp;
                for (var col = 0; col < width; col++)
                {
                    var x = region.Left + (col + 0.5) * mpp;
                    var h = heights[row * stride + col];
                    var east = heights[row * stride + col + 1];
                    var south = heights[(row + 1) * stride + col];
                    var distance = Math.Sqrt(x * x + z * z);
                    pixels[row * width + col] = WorldMapPalette.Shade(biomes[row * width + col], h, east, south, mpp, distance);
                }
            });
        }

        private WorldGeneratorPort ForkGenerator()
        {
            // A port handle is single-threaded; a fork shares the finished world and costs almost nothing.
            lock (_forkLock) return _generator.Fork();
        }
    }

    /// <summary>The map's colours: the in-game map's biome colours, lit by a hill-shade, with water by depth.</summary>
    internal static class WorldMapPalette
    {
        private static readonly (double R, double G, double B) Meadows = (146, 167, 92);
        private static readonly (double R, double G, double B) BlackForest = (96, 108, 58);
        private static readonly (double R, double G, double B) Swamp = (140, 104, 82);
        private static readonly (double R, double G, double B) Mountain = (196, 196, 204);
        private static readonly (double R, double G, double B) Plains = (222, 175, 118);
        private static readonly (double R, double G, double B) Mistlands = (78, 76, 86);
        private static readonly (double R, double G, double B) AshLands = (132, 42, 34);
        private static readonly (double R, double G, double B) DeepNorth = (226, 236, 246);
        private static readonly (double R, double G, double B) ShallowWater = (78, 128, 158);
        private static readonly (double R, double G, double B) DeepWater = (22, 46, 78);

        /// <summary>The colour the legend shows for open water.</summary>
        public static readonly (double R, double G, double B) Water = (44, 82, 118);

        public static (double R, double G, double B) ForBiome(Biome biome) => biome switch
        {
            Biome.Meadows => Meadows,
            Biome.BlackForest => BlackForest,
            Biome.Swamp => Swamp,
            Biome.Mountain => Mountain,
            Biome.Plains => Plains,
            Biome.Mistlands => Mistlands,
            Biome.AshLands => AshLands,
            Biome.DeepNorth => DeepNorth,
            _ => DeepWater,
        };

        public static uint Shade(Biome biome, float height, float east, float south, double metersPerPixel, double distance)
        {
            if (biome == Biome.None || distance > WorldMapRenderer.MapRadius) return 0u;

            double r, g, b;
            var depth = WorldMapRenderer.WaterLevel - height;
            if (depth > 0)
            {
                // Water: shallow shelves keep a hint of the ground under them, then darken towards the deep sea.
                var t = Math.Clamp(depth / 60.0, 0, 1);
                var water = Mix(ShallowWater, DeepWater, Math.Sqrt(t));
                if (biome != Biome.Ocean && depth < 4)
                {
                    water = Mix(ForBiome(biome), water, 0.55 + depth / 4 * 0.45);
                }
                (r, g, b) = water;
            }
            else
            {
                var land = ForBiome(biome == Biome.Ocean ? Biome.Meadows : biome);
                if (biome == Biome.Mountain)
                {
                    // Snow caps brighten with altitude.
                    land = Mix(land, (250, 250, 252), Math.Clamp((height - 120) / 120.0, 0, 1));
                }

                // Lambert hill-shade, light from the north-west. A flat pixel gets 1.
                var dx = (east - height) / metersPerPixel;
                var dz = (south - height) / metersPerPixel;
                var nx = -dx;
                var ny = dz;
                var length = Math.Sqrt(nx * nx + ny * ny + 1);
                const double lx = -0.5, ly = 0.5, lz = 0.7071;
                var light = (nx * lx + ny * ly + lz) / length / lz;
                var shade = Math.Clamp(0.35 + 0.65 * light, 0.55, 1.3);

                // A thin darker rim where the land meets the water reads as a coastline.
                if (east < WorldMapRenderer.WaterLevel || south < WorldMapRenderer.WaterLevel) shade *= 0.8;

                (r, g, b) = (land.R * shade, land.G * shade, land.B * shade);
            }

            // Fade the outer edge of the world ocean into the background.
            var alpha = Math.Clamp((WorldMapRenderer.MapRadius - distance) / 100.0, 0, 1);
            return Pack(r, g, b, alpha);
        }

        private static (double R, double G, double B) Mix((double R, double G, double B) a, (double R, double G, double B) b, double t)
            => (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

        private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

        /// <summary>Premultiplied BGRA, as Avalonia's <c>Bgra8888</c> premultiplied bitmaps store it.</summary>
        private static uint Pack(double r, double g, double b, double alpha)
        {
            var a = ToByte(alpha * 255);
            return (uint)(a << 24 | ToByte(r * alpha) << 16 | ToByte(g * alpha) << 8 | ToByte(b * alpha));
        }
    }
}
