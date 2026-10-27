using System;
using System.Collections.Generic;
using System.Linq;
using ValheimServerGUI.Game.WorldMap;
using ValheimServerGUI.ServerPlugin;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    /// <summary>The server plugin's reading of the game, against stand-ins shaped like Valheim 1.0's types.</summary>
    public sealed class GamePlayersTests
    {
        private static readonly DateTime Now = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void BuildJson_ReadsThePlayersAndTheStaticWorld()
        {
            var game = new GamePlayers(typeof(ZNet), typeof(ZNetPeer));
            Assert.True(game.CanReadPlayers);

            // Before the world loads there is no server yet.
            ZNet.Start(null);
            var empty = LivePlayerReader.Parse(game.BuildJson(Now));
            Assert.Null(empty.World);
            Assert.Empty(empty.Players);

            ZNet.Start(new World { m_name = "Croket Land" },
                new ZNetPeer { m_playerName = "Brutus", m_refPos = new Vector3 { x = 120.5f, y = 30, z = -42.25f }, m_publicRefPos = false },
                new ZNetPeer { m_playerName = "", m_refPos = new Vector3() }, // still connecting
                new ZNetPeer { m_playerName = "Ra\"gnar", m_refPos = new Vector3 { x = -3, z = 7 }, m_publicRefPos = true });
            var json = game.BuildJson(Now);

            Assert.Null(game.Problem);
            var snapshot = LivePlayerReader.Parse(json);
            Assert.Equal("Croket Land", snapshot.World);
            Assert.Equal(Now, snapshot.Time.ToUniversalTime());
            Assert.Equal(new[] { "Brutus", "Ra\"gnar" }, snapshot.Players.Select(p => p.Name));
            Assert.Equal(120.5f, snapshot.Players[0].X);
            Assert.Equal(-42.25f, snapshot.Players[0].Z);
            Assert.False(snapshot.Players[0].IsPublic);
            Assert.True(snapshot.Players[1].IsPublic);
        }

        [Fact]
        public void BuildJson_KeepsThePlayersWithoutAWorldName()
        {
            var game = new GamePlayers(typeof(OtherZNet), typeof(ZNetPeer));

            var snapshot = LivePlayerReader.Parse(game.BuildJson(Now));

            Assert.Null(game.Problem);
            Assert.Null(snapshot.World);
            Assert.Equal("Brutus", Assert.Single(snapshot.Players).Name);
        }

        [Fact]
        public void BuildJson_WritesNoPlayersForAnUnknownGame()
        {
            var game = new GamePlayers(null!, null!);

            Assert.False(game.CanReadPlayers);
            Assert.Empty(LivePlayerReader.Parse(game.BuildJson(Now)).Players);
        }

        // Stand-ins named and shaped like the game's (assembly_valheim, Valheim 1.0.20).
#pragma warning disable IDE1006, CS0649
        private struct Vector3 { public float x, y, z; }

        private sealed class World { public string? m_name; }

        private sealed class ZNetPeer
        {
            public string? m_playerName;
            public Vector3 m_refPos;
            public bool m_publicRefPos;
        }

        private sealed class ZNet
        {
            private static ZNet? m_instance;
            private static World? m_world;
            private readonly List<ZNetPeer> m_peers = new();

            public static ZNet? instance => m_instance;

            public List<ZNetPeer> GetPeers() => m_peers;

            public static void Start(World? world, params ZNetPeer[] peers)
            {
                m_world = world;
                m_instance = world is null ? null : new ZNet();
                m_instance?.m_peers.AddRange(peers);
            }
        }

        // A game whose world is not where Valheim 1.0 keeps it.
        private sealed class OtherZNet
        {
            private static readonly OtherZNet m_instance = new();

            public static OtherZNet instance => m_instance;

            public List<ZNetPeer> GetPeers() => new() { new ZNetPeer { m_playerName = "Brutus", m_refPos = new Vector3 { x = 1, z = 2 } } };
        }
#pragma warning restore IDE1006, CS0649
    }
}
