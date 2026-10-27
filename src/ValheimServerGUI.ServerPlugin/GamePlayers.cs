#nullable disable
using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace ValheimServerGUI.ServerPlugin
{
    /// <summary>
    /// Reads the connected players from the game's <c>ZNet</c>, by reflection, and writes them as the plugin's JSON:
    /// <code>
    /// {"version":1,"time":"2026-10-26T19:40:00Z","world":"Midgard",
    ///  "players":[{"name":"Ragnar","x":12.5,"z":-40.1,"public":true}]}
    /// </code>
    /// Needs neither BepInEx nor Unity, so it is tested against stand-in types. Each part is read on its own: a game
    /// change that breaks one (the world name, say) leaves the rest written, and is reported through
    /// <see cref="Problem"/>.
    /// </summary>
    internal sealed class GamePlayers
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        private readonly PropertyInfo _instance;
        private readonly MethodInfo _getPeers;
        private readonly FieldInfo _world;
        private readonly FieldInfo _worldName;
        private readonly FieldInfo _peerName;
        private readonly FieldInfo _peerRefPos;
        private readonly FieldInfo _peerPublic;
        private readonly FieldInfo _x;
        private readonly FieldInfo _z;

        /// <param name="znet">The game's <c>ZNet</c> type.</param>
        /// <param name="peer">The game's <c>ZNetPeer</c> type.</param>
        public GamePlayers(Type znet, Type peer)
        {
            _instance = znet?.GetProperty("instance", Any);
            _getPeers = znet?.GetMethod("GetPeers", Any, null, Type.EmptyTypes, null);
            _world = znet?.GetField("m_world", Any); // static in Valheim 1.0
            _worldName = _world?.FieldType.GetField("m_name", Any);
            _peerName = peer?.GetField("m_playerName", Any);
            _peerRefPos = peer?.GetField("m_refPos", Any);
            _peerPublic = peer?.GetField("m_publicRefPos", Any);
            _x = _peerRefPos?.FieldType.GetField("x", Any);
            _z = _peerRefPos?.FieldType.GetField("z", Any);
        }

        /// <summary>Whether the player list can be read at all.</summary>
        public bool CanReadPlayers => _instance != null && _getPeers != null && _peerName != null && _x != null && _z != null;

        /// <summary>What could not be read on the last <see cref="BuildJson"/>, or null.</summary>
        public string Problem { get; private set; }

        public string BuildJson(DateTime utcNow)
        {
            Problem = null;
            var json = new StringBuilder(256);
            json.Append("{\"version\":1,\"time\":\"").Append(utcNow.ToString("o", CultureInfo.InvariantCulture)).Append('"');

            var znet = Read("the server", () => _instance?.GetValue(null));
            json.Append(",\"world\":");
            AppendString(json, znet == null ? null : Read("the world name", () => WorldName(znet)));

            json.Append(",\"players\":[");
            var peers = znet == null || !CanReadPlayers ? null : Read("the players", () => _getPeers.Invoke(znet, null) as IEnumerable);
            if (peers != null)
            {
                var first = true;
                foreach (var peer in peers)
                {
                    var player = Read("a player", () => Player(peer));
                    if (player == null) continue;
                    if (!first) json.Append(',');
                    first = false;
                    json.Append(player);
                }
            }
            json.Append("]}");
            return json.ToString();
        }

        private string WorldName(object znet)
        {
            if (_world == null || _worldName == null) return null;
            var world = _world.GetValue(_world.IsStatic ? null : znet);
            return world == null ? null : _worldName.GetValue(world) as string;
        }

        private string Player(object peer)
        {
            var name = _peerName.GetValue(peer) as string;
            if (string.IsNullOrEmpty(name)) return null; // still connecting
            var position = _peerRefPos.GetValue(peer);
            var x = Convert.ToSingle(_x.GetValue(position), CultureInfo.InvariantCulture);
            var z = Convert.ToSingle(_z.GetValue(position), CultureInfo.InvariantCulture);
            var isPublic = _peerPublic?.GetValue(peer) is bool b && b;

            var json = new StringBuilder(64).Append("{\"name\":");
            AppendString(json, name);
            return json.Append(",\"x\":").Append(x.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"z\":").Append(z.ToString("R", CultureInfo.InvariantCulture))
                .Append(",\"public\":").Append(isPublic ? "true" : "false").Append('}').ToString();
        }

        private T Read<T>(string what, Func<T> read) where T : class
        {
            try
            {
                return read();
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException { InnerException: { } inner }) e = inner;
                Problem ??= "Could not read " + what + ": " + e.Message;
                return null;
            }
        }

        private static void AppendString(StringBuilder json, string text)
        {
            if (text == null)
            {
                json.Append("null");
                return;
            }

            json.Append('"');
            foreach (var c in text)
            {
                switch (c)
                {
                    case '"': json.Append("\\\""); break;
                    case '\\': json.Append("\\\\"); break;
                    default:
                        if (c < 0x20) json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else json.Append(c);
                        break;
                }
            }
            json.Append('"');
        }
    }
}
