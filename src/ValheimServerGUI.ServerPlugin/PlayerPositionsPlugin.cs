using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using BepInEx;

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
    /// game assembly to build. When a game change breaks a read, it logs the problem once and keeps writing what it can.</para>
    /// </summary>
    [BepInPlugin(Guid, "ValheimServerGUI player positions", "1.0.1")]
    public sealed class PlayerPositionsPlugin : BaseUnityPlugin
    {
        public const string Guid = "ispowky.valheimservergui.playerpositions";
        public const string FileName = "ValheimServerGUI.players.json";

        private const double IntervalSeconds = 2;

        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly HashSet<string> _reported = new HashSet<string>();
        private double _nextWrite;
        private string _path;
        private GamePlayers _players;

        private void Awake()
        {
            _path = Path.Combine(Paths.ConfigPath, FileName);
            _players = new GamePlayers(Type.GetType("ZNet, assembly_valheim"), Type.GetType("ZNetPeer, assembly_valheim"));
            if (!_players.CanReadPlayers)
            {
                Logger.LogWarning("The game's player list was not found; no player positions will be written.");
            }
            Logger.LogInfo("Writing player positions to " + _path);
        }

        private void Update()
        {
            var now = _clock.Elapsed.TotalSeconds;
            if (now < _nextWrite) return;
            _nextWrite = now + IntervalSeconds;

            // Each different problem is logged once; writing goes on regardless, every tick.
            try
            {
                Write(_players.BuildJson(DateTime.UtcNow));
                if (_players.Problem != null && _reported.Add(_players.Problem)) Logger.LogWarning(_players.Problem);
            }
            catch (Exception e)
            {
                var problem = "Could not write player positions: " + e.Message;
                if (_reported.Add(problem)) Logger.LogWarning(problem);
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

        /// <summary>Writes beside the file and swaps it in, so the app never reads half a file.</summary>
        private void Write(string json)
        {
            var temp = _path + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(_path)) File.Replace(temp, _path, null);
            else File.Move(temp, _path);
        }
    }
}
