using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ValheimServerGUI.Localization;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>What the world's cartography tables hold: everything players have shared on them.</summary>
    /// <param name="TableCount">How many tables with a shared map the save holds.</param>
    /// <param name="Pins">The pins shared on the tables (<see cref="WorldLocationKind.Pin"/> markers), each once.</param>
    public sealed record SharedWorldMap(int TableCount, WorldMapExploration Exploration, IReadOnlyList<WorldMapMarker> Pins);

    /// <summary>One cartography table's map: what it shows discovered, and its pins.</summary>
    public sealed record SharedMapTable(WorldMapExploration Exploration, IReadOnlyList<WorldMapMarker> Pins);

    /// <summary>
    /// Reads the map shared on a world's cartography tables from the world save, read-only and with every file opened
    /// shared, so a running server is never disturbed. This is the one map the server keeps on behalf of every
    /// player: each player who writes to a table adds what they have discovered (and what others shared with them).
    /// A player's own map is never sent to the server; it stays in their character save, on their computer.
    ///
    /// <para>From <c>MapTable.OnWrite</c> and <c>Minimap.GetSharedMapData</c>: a table keeps its map in its ZDO under
    /// <c>ZDOVars.s_data</c> as a byte array (int32 length + bytes), gzip of a <c>ZPackage</c>:</para>
    /// <code>
    /// int32 version          // 3 as of Valheim 1.0
    /// int32 pixelCount       // textureSize², 2048² in vanilla
    /// byte  explored × pixelCount   // one bool per minimap pixel, row 0 at the south
    /// int32 pinCount
    /// pinCount × {
    ///   int64  ownerId       // the player who placed it
    ///   string name          // 7-bit encoded length + UTF-8, as BinaryWriter writes it
    ///   float  x, y, z
    ///   int32  type          // Minimap.PinType
    ///   byte   checked       // crossed out
    ///   string author        // version 3 and later
    /// }
    /// </code>
    /// <para>ZDOs live in the world folder's <c>*.chunk</c> files, whose ZDO layout is not documented, so instead of
    /// walking it this looks for that byte array itself: a length-prefixed gzip member whose payload opens with the
    /// version and a square, even-sided pixel count and holds every pixel. Nothing else in a save matches that.
    /// Every chunk file in the folder is read, so a table in a chunk the server is just replacing is still found;
    /// an outdated copy can only add area that was discovered at some point. Pins that do not read as expected are
    /// left out, keeping the table's discovered area.</para>
    /// </summary>
    public static class SharedMapReader
    {
        private const int MinVersion = 2;
        private const int MaxVersion = 9;
        private const int MaxTextureSize = 8192;

        // A chunk file holds at most 100 000 ZDOs; refuse anything absurd before reading it into memory.
        private const long MaxChunkBytes = 512L * 1024 * 1024;

        private const int MaxPins = 100_000;
        private const int MaxNameBytes = 1024;

        // Two copies of one pin (the same pin shared on two tables) are within this many metres of each other.
        private const float SamePinMeters = 1f;

        // Text that is not valid UTF-8 means the bytes are not pins after all.
        private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

        /// <summary>
        /// Reads every cartography table in the world folder, or returns null when there is none (or the world has no
        /// chunked save). A chunk file that cannot be read, for example because the server deleted it after a save, is
        /// skipped.
        /// </summary>
        public static SharedWorldMap? ReadFromWorldFolder(DirectoryInfo worldFolder)
        {
            if (!worldFolder.Exists) return null;

            var tables = new List<SharedMapTable>();
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

            var combined = WorldMapExploration.Combine(tables.Select(t => t.Exploration));
            return combined is null ? null : new SharedWorldMap(tables.Count, combined, MergePins(tables));
        }

        /// <summary>Finds every cartography table map stored in one chunk file's bytes.</summary>
        public static IReadOnlyList<SharedMapTable> FindSharedMaps(byte[] chunk)
        {
            var maps = new List<SharedMapTable>();
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

        /// <summary>
        /// Every pin once: players who read a table take its pins into their own map and write them back to other
        /// tables, so most pins are on several. A pin crossed out on any table counts as crossed out.
        /// </summary>
        private static IReadOnlyList<WorldMapMarker> MergePins(IEnumerable<SharedMapTable> tables)
        {
            var pins = new List<WorldMapMarker>();
            foreach (var pin in tables.SelectMany(t => t.Pins))
            {
                var same = pins.FindIndex(p => p.PinIcon == pin.PinIcon && p.Name == pin.Name
                                               && Math.Abs(p.X - pin.X) <= SamePinMeters && Math.Abs(p.Z - pin.Z) <= SamePinMeters);
                if (same < 0) pins.Add(pin);
                else if (pin.IsChecked && !pins[same].IsChecked) pins[same] = pins[same] with { IsChecked = true };
            }
            return pins;
        }

        private static SharedMapTable? TryParse(byte[] chunk, int offset, int length)
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
                return new SharedMapTable(WorldMapExploration.FromMinimap(size, explored), ReadPins(gzip, version));
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                return null;
            }
        }

        /// <summary>The pins after the explored pixels, or none when they do not read as expected.</summary>
        private static IReadOnlyList<WorldMapMarker> ReadPins(Stream package, int version)
        {
            try
            {
                using var reader = new BinaryReader(package, StrictUtf8, leaveOpen: true);
                var count = reader.ReadInt32();
                if (count is < 0 or > MaxPins) return Array.Empty<WorldMapMarker>();

                var pins = new List<WorldMapMarker>(Math.Min(count, 1024));
                for (var i = 0; i < count; i++)
                {
                    reader.ReadInt64(); // owner
                    var name = ReadString(reader);
                    var x = reader.ReadSingle();
                    reader.ReadSingle(); // height
                    var z = reader.ReadSingle();
                    var type = reader.ReadInt32();
                    var isChecked = reader.ReadByte() != 0;
                    if (version >= 3) ReadString(reader); // author

                    if (!float.IsFinite(x) || !float.IsFinite(z) || Math.Abs(x) > WorldMapRenderer.MapRadius
                        || Math.Abs(z) > WorldMapRenderer.MapRadius)
                    {
                        return Array.Empty<WorldMapMarker>();
                    }
                    if (IconFor(type) is not { } icon) continue;
                    pins.Add(new WorldMapMarker(DisplayName(name), WorldLocationKind.Pin, x, z) { PinIcon = icon, IsChecked = isChecked });
                }
                return pins;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException or DecoderFallbackException)
            {
                return Array.Empty<WorldMapMarker>();
            }
        }

        /// <summary>A string as <c>BinaryWriter</c> writes it, refusing a length no pin name has.</summary>
        private static string ReadString(BinaryReader reader)
        {
            var length = reader.Read7BitEncodedInt();
            if (length is < 0 or > MaxNameBytes) throw new InvalidDataException("Pin text too long.");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return StrictUtf8.GetString(bytes);
        }

        /// <summary>
        /// The icon for a <c>Minimap.PinType</c>: Icon0 to Icon3 (0 to 3), Death (4), Bed (5), Icon4 (6), Shout (7),
        /// None (8), Boss (9), Player (10), RandomEvent (11), Ping (12), EventArea (13), Hildir1 to Hildir3 (14 to 16).
        /// The game never shares deaths, beds or the passing pins (shouts, pings, events); they are left out if met.
        /// A newer type is drawn as a plain pin.
        /// </summary>
        private static WorldMapPinIcon? IconFor(int type) => type switch
        {
            0 => WorldMapPinIcon.Fire,
            1 => WorldMapPinIcon.Home,
            2 => WorldMapPinIcon.Hammer,
            3 => WorldMapPinIcon.Dot,
            6 => WorldMapPinIcon.Rune,
            9 => WorldMapPinIcon.Boss,
            14 or 15 or 16 => WorldMapPinIcon.Quest,
            4 or 5 or 7 or 8 or 10 or 11 or 12 or 13 => null,
            _ => WorldMapPinIcon.Dot,
        };

        /// <summary>
        /// What a player typed, or for a pin the game placed (a Vegvisir's), the name its text token stands for. A token
        /// this app cannot name gives an empty name.
        /// </summary>
        private static string DisplayName(string name)
        {
            name = name.Trim();
            if (!name.StartsWith('$')) return name;
            return name.ToLowerInvariant() switch
            {
                "$enemy_eikthyr" => Strings.MapLocation_Eikthyr,
                "$enemy_gdking" => Strings.MapLocation_TheElder,
                "$enemy_bonemass" => Strings.MapLocation_Bonemass,
                "$enemy_dragon" => Strings.MapLocation_Moder,
                "$enemy_goblinking" => Strings.MapLocation_Yagluth,
                "$enemy_seekerqueen" => Strings.MapLocation_TheQueen,
                "$enemy_fader" => Strings.MapLocation_Fader,
                _ => string.Empty,
            };
        }
    }
}
