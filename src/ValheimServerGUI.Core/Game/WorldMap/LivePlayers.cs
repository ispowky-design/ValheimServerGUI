using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>A player connected to the server, where they are now.</summary>
    /// <param name="IsPublic">The player's own in-game "visible on the map" choice.</param>
    public sealed record LivePlayer(string Name, float X, float Z, bool IsPublic);

    /// <summary>What the server plugin last wrote: when, for which world, and who was where.</summary>
    public sealed record LivePlayerSnapshot(DateTime Time, string? World, IReadOnlyList<LivePlayer> Players)
    {
        /// <summary>How old a snapshot may be before it no longer counts: the plugin writes every 2 seconds while the server runs.</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(15);

        /// <summary>Whether the server was still writing it a moment ago (a stopped server leaves its last file behind).</summary>
        public bool IsCurrent(DateTime utcNow) => utcNow - Time <= MaxAge && Time - utcNow <= MaxAge;
    }

    /// <summary>
    /// Reads the connected players' positions that the app's server plugin (<c>ValheimServerGUI.ServerPlugin</c>)
    /// writes beside BepInEx's configuration, read-only and with the file opened shared.
    /// </summary>
    public static class LivePlayerReader
    {
        public const string FileName = "ValheimServerGUI.players.json";

        private const long MaxBytes = 1024 * 1024;

        /// <summary>Where the plugin writes for the server at <paramref name="serverExePath"/>.</summary>
        public static string FilePath(string serverExePath)
            => Path.Join(Path.GetDirectoryName(serverExePath), "BepInEx", "config", FileName);

        /// <summary>
        /// Reads the snapshot, or returns null when there is none (the plugin is not installed, or the server never
        /// ran with it). Throws <see cref="IOException"/> or <see cref="InvalidDataException"/> when it cannot be read.
        /// </summary>
        public static LivePlayerSnapshot? Read(string path)
        {
            if (!File.Exists(path)) return null;

            string json;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > MaxBytes) throw new InvalidDataException("The player positions file is too large.");
                using var reader = new StreamReader(stream);
                json = reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null; // swapped out between the check and the open
            }

            return Parse(json);
        }

        public static LivePlayerSnapshot Parse(string json)
        {
            try
            {
                var root = JObject.Parse(json);
                var time = root.Value<DateTime?>("time") ?? throw new InvalidDataException("The player positions file has no time.");
                if (time.Kind == DateTimeKind.Local) time = time.ToUniversalTime();
                else if (time.Kind == DateTimeKind.Unspecified) time = DateTime.SpecifyKind(time, DateTimeKind.Utc);

                var players = (root["players"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Select(p => new LivePlayer(
                        p.Value<string>("name") ?? string.Empty,
                        p.Value<float?>("x") ?? 0,
                        p.Value<float?>("z") ?? 0,
                        p.Value<bool?>("public") ?? false))
                    .Where(p => p.Name.Length > 0 && float.IsFinite(p.X) && float.IsFinite(p.Z))
                    .ToList();

                return new LivePlayerSnapshot(time, root.Value<string>("world"), players);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidDataException("The player positions file is malformed.", e);
            }
        }
    }
}
