using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>A character's map of one world: the areas it has discovered.</summary>
    public sealed record CharacterWorldMap(string CharacterName, string FilePath, WorldMapExploration Exploration);

    /// <summary>
    /// Reads the map a character has of a world from its save (<c>characters_local/&lt;name&gt;.fch</c>), the same
    /// discovered area the player sees in game. Read-only; the file is opened shared.
    ///
    /// <para>Layout, from <c>PlayerProfile.SavePlayerToDisk</c> and <c>Minimap.GetMapData</c> (ZPackage encoding:
    /// little-endian, 7-bit length-prefixed strings, byte arrays as int32 length + bytes):</para>
    /// <code>
    /// int32 length, byte × length            // the profile package
    ///   int32 version                        // 46 as of Valheim 1.0
    ///   stats (three shapes by version, see ReadStats)
    ///   bool firstSpawn                      // version &gt;= 40
    ///   int32 count, world × count:
    ///     int64 worldUid, bool + vec3 spawn, bool + vec3 logout, bool + vec3 death (&gt;= 30), vec3 home,
    ///     bool hasMap, [byte[] mapData]      // &gt;= 29
    ///   string name, ... (not read)
    /// mapData: int32 mapVersion, then (version &gt;= 7) int32 length + gzip of:
    ///   int32 textureSize, byte[size²] explored, byte[size²] exploredOthers, pins..., bool
    /// </code>
    /// <para>Ported from SeedLab's <c>CharacterReader</c> (MIT), which was checked against real 1.0 saves.</para>
    /// </summary>
    public static class CharacterMapReader
    {
        /// <summary>Metres per minimap pixel (<c>Minimap.m_pixelSize</c>).</summary>
        public const float PixelSize = 12f;

        private const int VersionOldest = 27;
        private const int VersionStats = 28;
        private const int VersionMapData = 29;
        private const int VersionDeathPoint = 30;
        private const int VersionStats2 = 38;
        private const int VersionFirstSpawn = 40;
        private const int VersionAbandonedDn = 44;
        private const int VersionDeepNorth = 46;
        private const int MapVersionNewExplore = 5;
        private const int MapVersionCompressed = 7;

        private const int MaxInflatedBytes = 64 * 1024 * 1024;

        /// <summary>The character saves in a Valheim data folder (<c>characters_local</c>, then the older <c>characters</c>).</summary>
        public static IEnumerable<FileInfo> FindCharacterFiles(DirectoryInfo dataFolder)
        {
            foreach (var sub in new[] { "characters_local", "characters" })
            {
                var folder = new DirectoryInfo(Path.Join(dataFolder.FullName, sub));
                if (!folder.Exists) continue;
                foreach (var file in folder.GetFiles("*.fch").OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }

        /// <summary>
        /// Reads the character's map of the world <paramref name="worldUid"/>, or null when the character has never
        /// been in that world (or has no map of it). Throws <see cref="IOException"/> or
        /// <see cref="InvalidDataException"/> when the file cannot be read.
        /// </summary>
        public static CharacterWorldMap? ReadWorldMap(FileInfo characterFile, long worldUid)
        {
            using var stream = new FileStream(
                characterFile.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var result = Read(stream, worldUid);
            return result is null ? null : result with { FilePath = characterFile.FullName };
        }

        public static CharacterWorldMap? Read(Stream stream, long worldUid)
        {
            try
            {
                using var file = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
                var length = file.ReadInt32();
                if (length < 0 || length > stream.Length - stream.Position)
                {
                    throw new InvalidDataException($"Character save declares a {length}-byte profile.");
                }
                var package = file.ReadBytes(length);
                var p = new BinaryReader(new MemoryStream(package, writable: false), Encoding.UTF8);

                var version = p.ReadInt32();
                if (version < VersionOldest || version > VersionDeepNorth)
                {
                    throw new InvalidDataException($"Character save version {version} is not supported.");
                }

                SkipStats(p, version);
                if (version >= VersionFirstSpawn) p.ReadBoolean();

                var worldCount = ReadCount(p, 8);
                byte[]? mapData = null;
                for (var i = 0; i < worldCount; i++)
                {
                    var uid = p.ReadInt64();
                    p.ReadBoolean(); SkipVector(p); // spawn
                    p.ReadBoolean(); SkipVector(p); // logout
                    if (version >= VersionDeathPoint)
                    {
                        p.ReadBoolean(); SkipVector(p); // death
                    }
                    SkipVector(p); // home
                    if (version >= VersionMapData && p.ReadBoolean())
                    {
                        var blob = ReadByteArray(p);
                        if (uid == worldUid) mapData = blob;
                    }
                }

                var name = p.ReadString();
                if (mapData is null) return null;

                return new CharacterWorldMap(name, string.Empty, ParseMapData(mapData));
            }
            catch (Exception e) when (e is EndOfStreamException or FormatException)
            {
                throw new InvalidDataException("Character save is truncated or malformed.", e);
            }
        }

        private static WorldMapExploration ParseMapData(byte[] blob)
        {
            var outer = new BinaryReader(new MemoryStream(blob, writable: false), Encoding.UTF8);
            var mapVersion = outer.ReadInt32();
            if (mapVersion < MapVersionNewExplore)
            {
                throw new InvalidDataException($"Character map version {mapVersion} is too old to read.");
            }

            var p = outer;
            if (mapVersion >= MapVersionCompressed)
            {
                var compressed = ReadByteArray(outer);
                p = new BinaryReader(new MemoryStream(Inflate(compressed), writable: false), Encoding.UTF8);
            }

            var size = p.ReadInt32();
            if (size <= 0 || size > 8192 || (size & 1) != 0)
            {
                throw new InvalidDataException($"Character map has an implausible size {size}.");
            }

            var pixels = size * size;
            var explored = p.ReadBytes(pixels);
            var others = p.ReadBytes(pixels);
            if (explored.Length != pixels || others.Length != pixels) throw new EndOfStreamException();

            return WorldMapExploration.FromCharacterMap(size, explored, others);
        }

        /// <summary>The stats section, in the three shapes <c>LoadPlayerFromDisk</c> branches on.</summary>
        private static void SkipStats(BinaryReader p, int version)
        {
            if (version >= VersionDeepNorth || version == VersionAbandonedDn)
            {
                var statCount = ReadCount(p, 4);
                var categories = ReadCount(p, 1);
                for (var i = 0; i < categories; i++)
                {
                    Skip(p, (long)statCount * 4);
                    SkipNameValueList(p); // known worlds
                    SkipNameValueList(p); // known world keys
                    SkipNameValueList(p); // known commands
                    var enemyTables = ReadCount(p, 4);
                    for (var k = 0; k < enemyTables; k++) SkipNameValueList(p);
                    for (var k = 0; k < 5; k++) SkipNameValueList(p); // pickups, crafts, pickables, food, pieces
                }
            }
            else if (version >= VersionStats2)
            {
                Skip(p, (long)ReadCount(p, 4) * 4);
            }
            else if (version >= VersionStats)
            {
                Skip(p, 16);
            }
        }

        private static void SkipNameValueList(BinaryReader p)
        {
            var count = ReadCount(p, 5);
            for (var i = 0; i < count; i++)
            {
                p.ReadString();
                p.ReadSingle();
            }
        }

        private static void SkipVector(BinaryReader p) => Skip(p, 12);

        private static void Skip(BinaryReader p, long bytes)
        {
            if (bytes < 0 || p.BaseStream.Position + bytes > p.BaseStream.Length) throw new EndOfStreamException();
            p.BaseStream.Position += bytes;
        }

        private static int ReadCount(BinaryReader p, int minItemBytes)
        {
            var count = p.ReadInt32();
            if (count < 0 || count > (p.BaseStream.Length - p.BaseStream.Position) / minItemBytes)
            {
                throw new InvalidDataException($"Character save declares {count} items past the end of the file.");
            }
            return count;
        }

        private static byte[] ReadByteArray(BinaryReader p)
        {
            var length = ReadCount(p, 1);
            return p.ReadBytes(length);
        }

        private static byte[] Inflate(byte[] compressed)
        {
            using var gzip = new GZipStream(new MemoryStream(compressed, writable: false), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                if (output.Length > MaxInflatedBytes) throw new InvalidDataException("Character map inflates past the size limit.");
            }
            return output.ToArray();
        }
    }
}
