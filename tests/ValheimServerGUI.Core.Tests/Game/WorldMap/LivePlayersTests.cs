using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using ValheimServerGUI.Game.WorldMap;
using Xunit;

namespace ValheimServerGUI.Core.Tests.Game.WorldMap
{
    /// <summary>The connected players' positions written by the server plugin, and installing that plugin.</summary>
    public sealed class LivePlayersTests : IDisposable
    {
        private readonly DirectoryInfo _server =
            Directory.CreateDirectory(Path.Join(Path.GetTempPath(), "vsg_liveplayers_" + Guid.NewGuid().ToString("N")));

        public void Dispose() => _server.Delete(true);

        private string ServerExe
        {
            get
            {
                var exe = Path.Join(_server.FullName, "valheim_server.exe");
                if (!File.Exists(exe)) File.WriteAllText(exe, "");
                return exe;
            }
        }

        private void AddBepInEx()
        {
            var core = Directory.CreateDirectory(Path.Join(_server.FullName, "BepInEx", "core"));
            File.WriteAllText(Path.Join(core.FullName, "BepInEx.dll"), "");
        }

        [Fact]
        public void Parse_ReadsWhatThePluginWrites()
        {
            // The plugin's own format (ValheimServerGUI.ServerPlugin.PlayerPositionsPlugin.BuildJson).
            const string json = "{\"version\":1,\"time\":\"2026-10-26T19:40:00.1234567Z\",\"world\":\"Midgard\",\"players\":[" +
                                "{\"name\":\"Ragnar \\\"Red\\\"\",\"x\":12.5,\"z\":-40.25,\"public\":true}," +
                                "{\"name\":\"Lagertha\",\"x\":-3000,\"z\":2000,\"public\":false}]}";

            var snapshot = LivePlayerReader.Parse(json);

            Assert.Equal(new DateTime(2026, 10, 26, 19, 40, 0, DateTimeKind.Utc).AddTicks(1234567), snapshot.Time);
            Assert.Equal(DateTimeKind.Utc, snapshot.Time.Kind);
            Assert.Equal("Midgard", snapshot.World);
            Assert.Equal(new[]
            {
                new LivePlayer("Ragnar \"Red\"", 12.5f, -40.25f, true),
                new LivePlayer("Lagertha", -3000, 2000, false),
            }, snapshot.Players);
        }

        [Fact]
        public void IsCurrent_OnlyWhileTheServerKeepsWriting()
        {
            var now = new DateTime(2026, 10, 26, 19, 40, 0, DateTimeKind.Utc);
            var snapshot = new LivePlayerSnapshot(now, null, Array.Empty<LivePlayer>());

            Assert.True(snapshot.IsCurrent(now.AddSeconds(5)));
            Assert.False(snapshot.IsCurrent(now.AddMinutes(1)));
            Assert.False(snapshot.IsCurrent(now.AddMinutes(-1))); // a clock far behind is not trusted either
        }

        [Fact]
        public void Read_HandlesAMissingOrBrokenFile()
        {
            var path = LivePlayerReader.FilePath(ServerExe);
            Assert.Equal(Path.Join(_server.FullName, "BepInEx", "config", "ValheimServerGUI.players.json"), path);
            Assert.Null(LivePlayerReader.Read(path));

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{\"players\":");
            Assert.Throws<InvalidDataException>(() => LivePlayerReader.Read(path));

            File.WriteAllText(path, "{\"players\":[]}");
            Assert.Throws<InvalidDataException>(() => LivePlayerReader.Read(path)); // no time

            File.WriteAllText(path, "{\"time\":\"2026-10-26T19:40:00Z\",\"players\":[{\"name\":\"\",\"x\":1,\"z\":2},{\"x\":1}]}");
            Assert.Empty(LivePlayerReader.Read(path)!.Players);
        }

        [Fact]
        public void Installer_FollowsTheServerFolder()
        {
            Assert.Equal(ServerPluginState.NoServer, ServerPluginInstaller.Check(null));
            Assert.Equal(ServerPluginState.NoServer, ServerPluginInstaller.Check(Path.Join(_server.FullName, "missing.exe")));
            Assert.Equal(ServerPluginState.NoBepInEx, ServerPluginInstaller.Check(ServerExe));
            Assert.Throws<InvalidOperationException>(() => ServerPluginInstaller.Install(ServerExe));

            AddBepInEx();
            Assert.Equal(ServerPluginState.NotInstalled, ServerPluginInstaller.Check(ServerExe));

            ServerPluginInstaller.Install(ServerExe);
            var installed = Path.Join(_server.FullName, "BepInEx", "plugins", "ValheimServerGUI", "ValheimServerGUI.ServerPlugin.dll");
            Assert.Equal(ServerPluginInstaller.PluginBytes(), File.ReadAllBytes(installed));
            Assert.Equal(ServerPluginState.Installed, ServerPluginInstaller.Check(ServerExe));

            File.WriteAllBytes(installed, new byte[] { 1, 2, 3 });
            Assert.Equal(ServerPluginState.Outdated, ServerPluginInstaller.Check(ServerExe));
        }

        [Fact]
        public void EmbeddedPlugin_IsTheBepInExPlugin()
        {
            var bytes = ServerPluginInstaller.PluginBytes();

            // Read as metadata only: it references BepInEx and Unity, which are not here.
            using var pe = new PEReader(new MemoryStream(bytes));
            var metadata = pe.GetMetadataReader();
            Assert.Equal("ValheimServerGUI.ServerPlugin", metadata.GetString(metadata.GetAssemblyDefinition().Name));

            var plugin = metadata.TypeDefinitions.Select(metadata.GetTypeDefinition)
                .Single(t => metadata.GetString(t.Name) == "PlayerPositionsPlugin");
            var attribute = metadata.GetCustomAttribute(Assert.Single(plugin.GetCustomAttributes()));
            var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
            Assert.Equal("BepInEx.BepInPlugin", metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name));

            var blob = metadata.GetBlobReader(attribute.Value);
            blob.ReadUInt16(); // prolog
            Assert.Equal("ispowky.valheimservergui.playerpositions", blob.ReadSerializedString());

            Assert.Contains(LivePlayerReader.FileName, Encoding.Unicode.GetString(bytes));
        }

        [Fact]
        public void WebServer_ServesThePlayersItIsGiven()
        {
            using var server = new WorldMapWebServer();
            Assert.EndsWith("[]", Encoding.UTF8.GetString(server.Respond("GET /players HTTP/1.1")));

            server.PublishPlayers(new[] { new LivePlayer("Ragnar", 1.5f, -2f, true) });

            var response = Encoding.UTF8.GetString(server.Respond("GET /players HTTP/1.1"));
            Assert.Contains("Content-Type: application/json", response);
            Assert.EndsWith("[{\"n\":\"Ragnar\",\"x\":1.5,\"z\":-2}]", response);
        }
    }
}
