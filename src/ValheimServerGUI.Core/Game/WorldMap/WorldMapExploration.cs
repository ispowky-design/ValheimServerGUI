using System;
using System.Collections.Generic;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>
    /// Which parts of a world have been discovered, as a square grid laid over the world: row 0 is the northern edge,
    /// and a cell is explored when its byte is non-zero. The map hides everything outside the explored cells.
    /// </summary>
    public sealed class WorldMapExploration
    {
        public WorldMapExploration(int size, double metersPerCell, double left, double top, byte[] explored)
        {
            if (size <= 0 || explored.Length != size * size) throw new ArgumentException("The grid does not match its size.", nameof(explored));
            Size = size;
            MetersPerCell = metersPerCell;
            Left = left;
            Top = top;
            Explored = explored;
        }

        /// <summary>Cells per side.</summary>
        public int Size { get; }

        public double MetersPerCell { get; }

        /// <summary>World X of the grid's western edge.</summary>
        public double Left { get; }

        /// <summary>World Z of the grid's northern edge.</summary>
        public double Top { get; }

        /// <summary>Row-major, north to south; non-zero = explored.</summary>
        public byte[] Explored { get; }

        public bool IsExplored(double x, double z)
        {
            var col = (int)Math.Floor((x - Left) / MetersPerCell);
            var row = (int)Math.Floor((Top - z) / MetersPerCell);
            return col >= 0 && row >= 0 && col < Size && row < Size && Explored[row * Size + col] != 0;
        }

        /// <summary>The world rectangle (west, south, east, north) around every explored cell, or null when none is.</summary>
        public (double West, double South, double East, double North)? ExploredBounds()
        {
            int minCol = Size, maxCol = -1, minRow = Size, maxRow = -1;
            for (var row = 0; row < Size; row++)
            {
                var offset = row * Size;
                for (var col = 0; col < Size; col++)
                {
                    if (Explored[offset + col] == 0) continue;
                    if (col < minCol) minCol = col;
                    if (col > maxCol) maxCol = col;
                    if (row < minRow) minRow = row;
                    if (row > maxRow) maxRow = row;
                }
            }

            if (maxCol < 0) return null;
            return (Left + minCol * MetersPerCell, Top - (maxRow + 1) * MetersPerCell,
                Left + (maxCol + 1) * MetersPerCell, Top - minRow * MetersPerCell);
        }

        /// <summary>
        /// The zones the server has generated, from the world save: an approximation of where players have been, one
        /// 64 m cell per zone.
        /// </summary>
        public static WorldMapExploration FromGeneratedZones(IEnumerable<(short X, short Y)> zones)
        {
            var radius = (int)Math.Ceiling(WorldMapRenderer.MapRadius / WorldMapSaveData.ZoneSize);
            var size = 2 * radius + 1;
            var explored = new byte[size * size];
            foreach (var (x, y) in zones)
            {
                var col = x + radius;
                var row = radius - y;
                if (col >= 0 && col < size && row >= 0 && row < size) explored[row * size + col] = 1;
            }

            // Zone (x, y) covers x*64 - 32 to x*64 + 32 (ZoneSystem.GetZone).
            var extent = (radius + 0.5) * WorldMapSaveData.ZoneSize;
            return new WorldMapExploration(size, WorldMapSaveData.ZoneSize, -extent, extent, explored);
        }

        /// <summary>
        /// A character's own map of the world (<see cref="CharacterMapReader"/>): the game's minimap grid, 12 m per
        /// pixel, centred on the world origin, with row 0 at the south. Both what the character explored and what was
        /// shared with them at a cartography table count.
        /// </summary>
        public static WorldMapExploration FromCharacterMap(int textureSize, byte[] explored, byte[] exploredOthers)
        {
            var size = textureSize;
            var grid = new byte[size * size];
            for (var row = 0; row < size; row++)
            {
                var source = row * size;
                var target = (size - 1 - row) * size;
                for (var col = 0; col < size; col++)
                {
                    if (explored[source + col] != 0 || exploredOthers[source + col] != 0) grid[target + col] = 1;
                }
            }

            // Minimap.WorldToPixel rounds x / 12 + size / 2, so pixel col covers (col - size/2) * 12 ± 6 m.
            var half = size / 2;
            var pixel = CharacterMapReader.PixelSize;
            return new WorldMapExploration(size, pixel, (-half - 0.5) * pixel, (size - 1 - half + 0.5) * pixel, grid);
        }
    }
}
