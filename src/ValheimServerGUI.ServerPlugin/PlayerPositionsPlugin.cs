using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

namespace ValheimServerGUI.ServerPlugin
{
    /// <summary>
    /// Writes, every couple of seconds, where the players connected to this dedicated server are, so ValheimServerGUI
    /// can show them on its World Map. The file is <c>BepInEx/config/ValheimServerGUI.players.json</c>:
    /// <code>
    /// {"version":1,"time":"2026-10-26T19:40:00Z","world":"Midgard",
    ///  "players":[{"name":"Ragnar","x":12.5,"z":-40.1,"public":true}]}
    /// </code>
    /// <c>public</c> is the player's own in-game "visible on the map" choice. The position is the one each client
    /// reports to the server (<c>ZNetPeer.m_refPos</c>), which the server keeps whatever that choice is.
    ///
    /// <para>Read-only towards the game: it only reads <c>ZNet</c>'s peer list, by reflection, so it needs no
    /// game assembly to build and does nothing when the game's names change except log once and write no players.</para>
    /// </summary>
    [BepInPlugin(Guid, "ValheimServerGUI player positions", "1.0.0")]
    public sealed class PlayerPositionsPlugin : BaseUnityPlugin
    {
        public const string Guid = "ispowky.valheimservergui.playerpositions";
        public const string FileName = "ValheimServerGUI.players.json";

        private const double IntervalSeconds = 2;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private double _nextWrite;
        private string _path;
        private bool _warned;

        private PropertyInfo _instance;
        private MethodInfo _getPeers;
        private FieldInfo _world;
        private FieldInfo _worldName;
        private FieldInfo _peerName;
        private FieldInfo _peerRefPos;
        private FieldInfo _peerPublic;

        private void Awake()
        {
            _path = Path.Combine(Paths.ConfigPath, FileName);

            var znet = Type.GetType("ZNet, assembly_valheim");
            var peer = Type.GetType("ZNetPeer, assembly_valheim");
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            _instance = znet?.GetProperty("instance", any);
            _getPeers = znet?.GetMethod("GetPeers", any, null, Type.EmptyTypes, null);
            _world = znet?.GetField("m_world", any);
            _worldName = _world?.FieldType.GetField("m_name", any);
            _peerName = peer?.GetField("m_playerName", any);
            _peerRefPos = peer?.GetField("m_refPos", any);
            _peerPublic = peer?.GetField("m_publicRefPos", any);

            if (_instance == null || _getPeers == null || _peerName == null || _peerRefPos == null)
            {
                Logger.LogWarning("The game's player list was not found; player positions will not be written.");
            }
        }

        private void Update()
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (now < _nextWrite) return;
            _nextWrite = now + IntervalSeconds;

            try
            {
                Write(BuildJson());
            }
            catch (Exception e)
            {
                if (_warned) return;
                _warned = true;
                Logger.LogWarning("Could not write player positions: " + e.Message);
            }
        }

        private void OnDestroy()
        {
            try
            {
                if (_path != null && File.Exists(_path)) File.Delete(_path);
            }
            catch (IOException)
            {
                // The app treats an old file as "no players" anyway.
            }
        }

        private string BuildJson()
        {
            var json = new StringBuilder(256);
            json.Append("{\"version\":1,\"time\":\"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('"');

            var znet = _instance?.GetValue(null);
            var world = znet == null ? null : _worldName?.GetValue(_world.IsStatic ? null : znet) as string;
            json.Append(",\"world\":");
            AppendString(json, world);

            json.Append(",\"players\":[");
            if (znet != null && _getPeers?.Invoke(znet, null) is IEnumerable peers)
            {
                var first = true;
                foreach (var peer in peers)
                {
                    var name = _peerName.GetValue(peer) as string;
                    if (string.IsNullOrEmpty(name)) continue; // still connecting
                    var position = (Vector3)_peerRefPos.GetValue(peer);
                    var isPublic = _peerPublic?.GetValue(peer) is bool b && b;

                    if (!first) json.Append(',');
                    first = false;
                    json.Append("{\"name\":");
                    AppendString(json, name);
                    json.Append(",\"x\":").Append(position.x.ToString("R", CultureInfo.InvariantCulture))
                        .Append(",\"z\":").Append(position.z.ToString("R", CultureInfo.InvariantCulture))
                        .Append(",\"public\":").Append(isPublic ? "true" : "false").Append('}');
                }
            }
            json.Append("]}");
            return json.ToString();
        }

        /// <summary>Writes beside the file and swaps it in, so the app never reads half a file.</summary>
        private void Write(string json)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
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
