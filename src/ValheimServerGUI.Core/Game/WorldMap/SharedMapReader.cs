using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>What the world's cartography tables hold: everything players have shared on them.</summary>
    /// <param name="TableCount">How many tables with a shared map the save holds.</param>
    public sealed record SharedWorldMap(int TableCount, WorldMapExploration Exploration);

    /// <summary>
    /// Reads the map shared on a world's cartography tables from the world save, read-only and with every file opened
    /// shared, so a running server is never disturbed. This is the one map the server keeps on behalf of every
    /// player: each player who writes to a table adds what they have discovered (and what others shared with them).
    /// A player's own map is never sent to the server; it stays in their character save, on their computer.
    ///
    /// <para>From <c>MapTable.OnWrite</c> and <c>Minimap.GetSharedMapData</c>: a table keeps its map in its ZDO under
    /// <c>ZDOVars.s_data</c> as a byte array (int32 length + bytes), gzip of:</para>
    /// <code>
    /// int32 version          // 3 as of Valheim 1.0
    /// int32 pixelCount       // textureSize², 2048² in vanilla
    /// byte  explored × pixelCount   // one bool per minimap pixel, row 0 at the south
    /// pins... (not read)
    /// </code>
    /// <para>ZDOs live in the world folder's <c>*.chunk</c> files, whose ZDO layout is not documented, so instead of
    /// walking it this looks for that byte array itself: a length-prefixed gzip member whose payload opens with the
    /// version and a square, even-sided pixel count and holds every pixel. Nothing else in a save matches that.
    /// Every chunk file in the folder is read, so a table in a chunk the server is just replacing is still found;
    /// an outdated copy can only add area that was discovered at some point.</para>
    /// </summary>
    public static class SharedMapReader
    {
        private const int MinVersion = 2;
        private const int MaxVersion = 9;
        private const int MaxTextureSize = 8192;

        // A chunk file holds at most 100 000 ZDOs; refuse anything absurd before reading it into memory.
        private const long MaxChunkBytes = 512L * 1024 * 1024;

        /// <summary>
        /// Reads every cartography table in the world folder, or returns null when there is none (or the world has no
        /// chunked save). A chunk file that cannot be read, for example because the server deleted it after a save, is
        /// skipped.
        /// </summary>
        public static SharedWorldMap? ReadFromWorldFolder(DirectoryInfo worldFolder)
        {
            if (!worldFolder.Exists) return null;

            var tables = new List<WorldMapExploration>();
            foreach (var file in worldFolder.GetFiles("*.chunk").OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                byte[] bytes;
                try
                {
                    if (file.Length > MaxChunkBytes) continue;
                    using var stream = new FileStream(
                        file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                tables.AddRange(FindSharedMaps(bytes));
            }

            var combined = WorldMapExploration.Combine(tables);
            return combined is null ? null : new SharedWorldMap(tables.Count, combined);
        }

        /// <summary>Finds every cartography table map stored in one chunk file's bytes.</summary>
        public static IReadOnlyList<WorldMapExploration> FindSharedMaps(byte[] chunk)
        {
            var maps = new List<WorldMapExploration>();
            var span = chunk.AsSpan();
            ReadOnlySpan<byte> gzipMagic = [0x1F, 0x8B, 0x08];

            var position = 4;
            while (position < chunk.Length)
            {
                var found = span[position..].IndexOf(gzipMagic);
                if (found < 0) break;
                var start = position + found;
                position = start + 1;

                var length = BitConverter.ToInt32(chunk, start - 4);
                if (length < 18 || length > chunk.Length - start) continue;

                if (TryParse(chunk, start, length) is { } map)
                {
                    maps.Add(map);
                    position = start + length;
                }
            }
            return maps;
        }

        private static WorldMapExploration? TryParse(byte[] chunk, int offset, int length)
        {
            try
            {
                using var gzip = new GZipStream(new MemoryStream(chunk, offset, length, writable: false), CompressionMode.Decompress);
                Span<byte> header = stackalloc byte[8];
                gzip.ReadExactly(header);

                var version = BitConverter.ToInt32(header[..4]);
                var pixels = BitConverter.ToInt32(header[4..]);
                if (version < MinVersion || version > MaxVersion) return null;

                var size = (int)Math.Round(Math.Sqrt(pixels));
                if (size <= 0 || size > MaxTextureSize || (size & 1) != 0 || size * size != pixels) return null;

                var explored = new byte[pixels];
                gzip.ReadExactly(explored);
                return WorldMapExploration.FromMinimap(size, explored);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                return null;
            }
        }
    }
}
