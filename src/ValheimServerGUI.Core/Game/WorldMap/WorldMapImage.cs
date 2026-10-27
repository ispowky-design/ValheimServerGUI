using System;
using System.Threading;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>A drawn piece of the world: opaque BGRA pixels, row 0 at the north, covering <see cref="Region"/>.</summary>
    public sealed record WorldMapPicture(int Width, int Height, WorldMapRegion Region, uint[] Pixels)
    {
        /// <summary>Where a world point falls in the picture, as fractions of its width and height (0 to 1 inside it).</summary>
        public (double X, double Y) ToFraction(double x, double z)
            => ((x - Region.Left) / (Width * Region.MetersPerPixel), (Region.Top - z) / (Height * Region.MetersPerPixel));
    }

    /// <summary>
    /// Draws the discovered part of a world as one picture, for sharing it outside the app: framed on what has been
    /// discovered, with everything else covered in parchment as on the in-game map (see <see cref="WorldMapFog"/>).
    /// </summary>
    public static class WorldMapImage
    {
        /// <summary>The parchment's base colour, as premultiplied BGRA; the paper itself is mottled around it.</summary>
        public const uint Parchment = 0xFFD8C49A;

        /// <summary>The finest detail drawn, in metres per pixel; a small discovered area is not blown up past it.</summary>
        public const double MinMetersPerPixel = 2;

        private const double Margin = 1.1;
        private const double MinSpan = 400;

        /// <summary>
        /// Draws the area <paramref name="exploration"/> has discovered (with a small margin), or the whole world when it
        /// is null or nothing is discovered yet. The longer side is at most <paramref name="maxPixels"/>.
        /// </summary>
        public static WorldMapPicture Render(WorldMapRenderer renderer, WorldMapExploration? exploration, int maxPixels = 2048,
            CancellationToken cancellationToken = default)
        {
            if (maxPixels < 16) throw new ArgumentOutOfRangeException(nameof(maxPixels));

            var (west, south, east, north) = Frame(exploration?.ExploredBounds());
            var mpp = Math.Max(MinMetersPerPixel, Math.Max(east - west, north - south) / maxPixels);
            var width = Math.Max(1, (int)Math.Ceiling((east - west) / mpp));
            var height = Math.Max(1, (int)Math.Ceiling((north - south) / mpp));
            var region = new WorldMapRegion(west, north, mpp);

            var pixels = new uint[width * height];
            renderer.Render(region, width, height, pixels, cancellationToken);

            // Outside the world disc the renderer leaves pixels transparent; the picture is opaque.
            if (exploration is null) WorldMapFog.FillTransparent(region, width, height, pixels);
            else new WorldMapFog(exploration).Cover(region, width, height, pixels);

            return new WorldMapPicture(width, height, region, pixels);
        }

        private static (double West, double South, double East, double North) Frame(
            (double West, double South, double East, double North)? bounds)
        {
            const double limit = WorldMapRenderer.MapRadius;
            if (bounds is not { } b) return (-limit, -limit, limit, limit);

            var centerX = (b.West + b.East) / 2;
            var centerZ = (b.South + b.North) / 2;
            var halfX = Math.Max(MinSpan, (b.East - b.West) * Margin) / 2;
            var halfZ = Math.Max(MinSpan, (b.North - b.South) * Margin) / 2;
            return (Math.Max(-limit, centerX - halfX), Math.Max(-limit, centerZ - halfZ),
                Math.Min(limit, centerX + halfX), Math.Min(limit, centerZ + halfZ));
        }
    }
}
