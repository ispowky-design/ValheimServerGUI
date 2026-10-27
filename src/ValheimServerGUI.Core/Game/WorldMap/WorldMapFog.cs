using System;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>
    /// The parchment that covers what nobody has discovered, as on the in-game map: mottled like old paper, browner
    /// along the edge of the discovered area, and meeting it softly rather than in square cells.
    /// </summary>
    public sealed class WorldMapFog
    {
        /// <summary>The paper's base colour.</summary>
        public static readonly (double R, double G, double B) Paper = (216, 196, 154);

        // How far, in exploration cells, the paper browns towards the discovered area.
        private const int EdgeCells = 3;

        private readonly WorldMapExploration _exploration;

        // Per cell: the share of explored cells around it (a box blur of the explored grid).
        private readonly float[] _near;

        public WorldMapFog(WorldMapExploration exploration)
        {
            _exploration = exploration;
            _near = Blur(exploration);
        }

        /// <summary>
        /// One premultiplied BGRA pixel per exploration cell, row 0 at the north: clear where explored, paper elsewhere.
        /// Scaled up smoothly, it gives a soft edge.
        /// </summary>
        public uint[] CellPixels()
        {
            var size = _exploration.Size;
            var mpc = _exploration.MetersPerCell;
            var pixels = new uint[size * size];
            for (var row = 0; row < size; row++)
            {
                var z = _exploration.Top - (row + 0.5) * mpc;
                for (var col = 0; col < size; col++)
                {
                    var i = row * size + col;
                    if (_exploration.Explored[i] != 0) continue;
                    var (r, g, b) = PaperAt(_exploration.Left + (col + 0.5) * mpc, z, _near[i]);
                    pixels[i] = WorldMapPalette.Pack(r, g, b, 1);
                }
            }
            return pixels;
        }

        /// <summary>
        /// Covers a drawn picture's undiscovered ground with paper, blending across the edge of the discovered area
        /// pixel by pixel, so the edge stays smooth however close it is drawn. With <paramref name="opaque"/>, whatever
        /// the terrain left transparent (outside the world disc) becomes paper too; without, it stays transparent.
        /// </summary>
        public void Cover(WorldMapRegion region, int width, int height, uint[] pixels, bool opaque = true)
        {
            var mpp = region.MetersPerPixel;
            for (var row = 0; row < height; row++)
            {
                var z = region.Top - (row + 0.5) * mpp;
                for (var col = 0; col < width; col++)
                {
                    var x = region.Left + (col + 0.5) * mpp;
                    var i = row * width + col;
                    // The edge follows the blurred grid, so it runs smooth instead of along the cells. Discovered areas
                    // are made of the game's 100 m circles, far wider than the blur, so none is lost to it.
                    var near = Sample(x, z);
                    var visible = WorldMapPalette.SmoothStep(0.3, 0.6, near);
                    pixels[i] = OverPaper(pixels[i], PaperAt(x, z, near), visible, opaque);
                }
            }
        }

        /// <summary>The paper alone under a picture's transparent pixels, for a map shown without fog.</summary>
        public static void FillTransparent(WorldMapRegion region, int width, int height, uint[] pixels)
        {
            var mpp = region.MetersPerPixel;
            for (var row = 0; row < height; row++)
            {
                var z = region.Top - (row + 0.5) * mpp;
                for (var col = 0; col < width; col++)
                {
                    var i = row * width + col;
                    if (pixels[i] >> 24 == 0xFF) continue;
                    pixels[i] = OverPaper(pixels[i], PaperAt(region.Left + (col + 0.5) * mpp, z, 0), 1, opaque: true);
                }
            }
        }

        /// <summary>The paper's colour at a world point; <paramref name="near"/> is how much discovered ground is close.</summary>
        private static (double R, double G, double B) PaperAt(double x, double z, double near)
        {
            var mottle = 0.6 * WorldMapNoise.Value(x / 260, z / 260) + 0.4 * WorldMapNoise.Value(x / 45, z / 45);
            var edge = Math.Clamp(near * 2.5, 0, 1);
            var shade = 0.95 + 0.08 * mottle - 0.14 * edge;
            return (Paper.R * shade, Paper.G * shade, Paper.B * (shade - 0.05 * edge));
        }

        /// <summary>
        /// A premultiplied terrain pixel shown as much as <paramref name="visible"/> says, over paper: under all of it
        /// when <paramref name="opaque"/>, else only within the terrain's own coverage.
        /// </summary>
        private static uint OverPaper(uint terrain, (double R, double G, double B) paper, double visible, bool opaque)
        {
            var alpha = (terrain >> 24) / 255.0;
            var under = opaque ? 1 - alpha * visible : alpha * (1 - visible);
            var r = ((terrain >> 16) & 0xFF) * visible + paper.R * under;
            var g = ((terrain >> 8) & 0xFF) * visible + paper.G * under;
            var b = (terrain & 0xFF) * visible + paper.B * under;
            var a = opaque ? 255 : terrain >> 24;
            return a << 24 | Byte(r) << 16 | Byte(g) << 8 | Byte(b);
        }

        private static uint Byte(double v) => (uint)Math.Clamp(Math.Round(v), 0, 255);

        /// <summary>The blurred explored share at a point, blended between the four nearest cells.</summary>
        private double Sample(double x, double z)
        {
            var size = _exploration.Size;
            var fx = (x - _exploration.Left) / _exploration.MetersPerCell - 0.5;
            var fz = (_exploration.Top - z) / _exploration.MetersPerCell - 0.5;
            var col = (int)Math.Floor(fx);
            var row = (int)Math.Floor(fz);
            var tx = fx - col;
            var tz = fz - row;

            double near = 0;
            for (var dz = 0; dz <= 1; dz++)
            {
                for (var dx = 0; dx <= 1; dx++)
                {
                    var c = col + dx;
                    var r = row + dz;
                    if (c < 0 || r < 0 || c >= size || r >= size) continue;
                    near += _near[r * size + c] * (dx == 0 ? 1 - tx : tx) * (dz == 0 ? 1 - tz : tz);
                }
            }
            return near;
        }

        /// <summary>A separable box blur of the explored grid, <see cref="EdgeCells"/> cells each way.</summary>
        private static float[] Blur(WorldMapExploration exploration)
        {
            var size = exploration.Size;
            var across = new float[size * size];
            var result = new float[size * size];
            const float share = 1f / (2 * EdgeCells + 1);

            for (var row = 0; row < size; row++)
            {
                var offset = row * size;
                var sum = 0;
                for (var col = -EdgeCells; col < size; col++)
                {
                    var add = col + EdgeCells;
                    if (add < size && exploration.Explored[offset + add] != 0) sum++;
                    var drop = col - EdgeCells - 1;
                    if (drop >= 0 && exploration.Explored[offset + drop] != 0) sum--;
                    if (col >= 0) across[offset + col] = sum * share;
                }
            }

            for (var col = 0; col < size; col++)
            {
                var sum = 0f;
                for (var row = -EdgeCells; row < size; row++)
                {
                    var add = row + EdgeCells;
                    if (add < size) sum += across[add * size + col];
                    var drop = row - EdgeCells - 1;
                    if (drop >= 0) sum -= across[drop * size + col];
                    if (row >= 0) result[row * size + col] = Math.Max(0, sum * share);
                }
            }
            return result;
        }
    }
}
