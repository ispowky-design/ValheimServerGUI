using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>One location instance a world save holds: the prefab's stable hash and where it is.</summary>
    /// <param name="X">World X (east).</param>
    /// <param name="Z">World Z (north).</param>
    /// <param name="Placed">Whether the game has built it yet, which only happens once a player comes near.</param>
    public readonly record struct WorldSaveLocation(int PrefabHash, float X, float Z, bool Placed);

    /// <summary>
    /// What the world map reads from a world's database save: every location instance the world registered, and the
    /// 64 m zones the server has generated so far (the ones players have been near).
    /// </summary>
    public sealed record WorldMapSaveData(
        IReadOnlyList<WorldSaveLocation> Locations,
        IReadOnlyList<(short X, short Y)> GeneratedZones)
    {
        /// <summary>The size of one game zone, in metres.</summary>
        public const float ZoneSize = 64f;
    }

    /// <summary>
    /// Reads the locations and generated zones from a world's database save (<c>worlds_local/&lt;World&gt;/_main.&lt;N&gt;.db2</c>,
    /// Valheim 1.0), read-only and with the file shared, so a running server is never disturbed.
    ///
    /// <para>Layout, from <c>ZNet.SaveWorldThread</c> and <c>ZoneSystem.Save</c> (BinaryWriter encoding):</para>
    /// <code>
    /// int32  version                  // 41 as of Valheim 1.0
    /// double netTime
    /// int32  compressedLength, byte × compressedLength   // gzip of the ZoneSystem block:
    ///   int32 count, (int16 x, int16 y) × count          // generated zones
    ///   int32 locationVersion
    ///   int32 count, string × count                      // global keys
    ///   bool  locationsGenerated
    ///   int32 count, (int32 prefabHash, float x, float y, float z, bool placed) × count
    /// (random-event and persistent-event blocks follow; not read)
    /// </code>
    /// <para>The ZoneSystem block must be consumed exactly, so a changed layout fails instead of returning garbage.
    /// Saves from before 1.0 (a single <c>.db</c> file) are not read.</para>
    /// </summary>
    public static class WorldMapSaveReader
    {
        /// <summary>The first world version whose database is a <c>.db2</c> with a gzip ZoneSystem block.</summary>
        public const int MinVersion = 41;

        // A ZoneSystem block for a full world is well under a megabyte; refuse anything absurd before inflating it.
        private const int MaxCompressedBytes = 64 * 1024 * 1024;
        private const int MaxInflatedBytes = 256 * 1024 * 1024;

        private const int LocationBytes = 17;

        /// <summary>
        /// Reads the database that belongs to the world folder's newest complete save, or returns null when the world
        /// has no complete 1.0 save yet. Throws <see cref="IOException"/> or <see cref="InvalidDataException"/> when the
        /// save cannot be read.
        /// </summary>
        public static WorldMapSaveData? ReadFromWorldFolder(DirectoryInfo worldFolder)
        {
            var meta = WorldMetadataReader.FindLatestCompleteSave(worldFolder);
            if (meta is null) return null;

            var db = new FileInfo(Path.ChangeExtension(meta.FullName, ".db2"));
            if (!db.Exists) return null;

            // Share delete as well as write: the server deletes the previous save's files right after it commits a
            // new one (see WorldMetadataReader).
            using var stream = new FileStream(
                db.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Read(stream);
        }

        /// <summary>Reads a <c>.db2</c> stream. Throws <see cref="InvalidDataException"/> when it does not match the layout.</summary>
        public static WorldMapSaveData Read(Stream stream)
        {
            try
            {
                using var file = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                var version = file.ReadInt32();
                if (version < MinVersion || version > 1000)
                {
                    throw new InvalidDataException($"World database version {version} is not a Valheim 1.0 save.");
                }

                file.ReadDouble(); // netTime
                var compressedLength = file.ReadInt32();
                if (compressedLength <= 0 || compressedLength > MaxCompressedBytes)
                {
                    throw new InvalidDataException($"World database declares a {compressedLength}-byte zone block.");
                }

                var compressed = file.ReadBytes(compressedLength);
                if (compressed.Length != compressedLength) throw new EndOfStreamException();

                return ParseZoneSystem(Inflate(compressed));
            }
            catch (Exception e) when (e is EndOfStreamException or FormatException)
            {
                throw new InvalidDataException("World database is truncated or malformed.", e);
            }
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
                if (output.Length > MaxInflatedBytes)
                {
                    throw new InvalidDataException("World database zone block inflates past the size limit.");
                }
            }
            return output.ToArray();
        }

        private static WorldMapSaveData ParseZoneSystem(byte[] block)
        {
            using var reader = new BinaryReader(new MemoryStream(block, writable: false), Encoding.UTF8);
            long Remaining() => block.Length - reader.BaseStream.Position;

            int ReadCount(int minItemBytes)
            {
                var count = reader.ReadInt32();
                if (count < 0 || count > Remaining() / minItemBytes)
                {
                    throw new InvalidDataException($"World database declares {count} items but only {Remaining()} bytes remain.");
                }
                return count;
            }

            var zoneCount = ReadCount(4);
            var zones = new List<(short X, short Y)>(zoneCount);
            for (var i = 0; i < zoneCount; i++) zones.Add((reader.ReadInt16(), reader.ReadInt16()));

            reader.ReadInt32(); // locationVersion

            var keyCount = ReadCount(1);
            for (var i = 0; i < keyCount; i++) reader.ReadString();

            reader.ReadBoolean(); // locationsGenerated

            var locationCount = ReadCount(LocationBytes);
            var locations = new List<WorldSaveLocation>(locationCount);
            for (var i = 0; i < locationCount; i++)
            {
                var hash = reader.ReadInt32();
                var x = reader.ReadSingle();
                reader.ReadSingle(); // altitude
                var z = reader.ReadSingle();
                var placed = reader.ReadBoolean();
                locations.Add(new WorldSaveLocation(hash, x, z, placed));
            }

            if (Remaining() != 0)
            {
                throw new InvalidDataException(
                    $"World database zone block has {Remaining()} unread bytes after the locations; the layout is not recognized.");
            }

            return new WorldMapSaveData(locations, zones);
        }
    }
}
