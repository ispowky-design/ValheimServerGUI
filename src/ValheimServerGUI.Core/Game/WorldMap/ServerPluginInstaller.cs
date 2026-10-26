using System;
using System.IO;
using System.Linq;

namespace ValheimServerGUI.Game.WorldMap
{
    /// <summary>Where the app's server plugin stands for a server install.</summary>
    public enum ServerPluginState
    {
        /// <summary>No server is set up (no server executable).</summary>
        NoServer,

        /// <summary>The server folder has no BepInEx, which the plugin needs to load.</summary>
        NoBepInEx,

        NotInstalled,

        /// <summary>Installed, but not the version this app carries.</summary>
        Outdated,

        Installed,
    }

    /// <summary>
    /// Installs the app's server plugin (<c>ValheimServerGUI.ServerPlugin</c>, which writes the connected players'
    /// positions for the World Map) into a dedicated server's BepInEx plugins folder. Only ever on the user's request:
    /// it changes the server's install, and takes effect when the server next starts.
    /// </summary>
    public static class ServerPluginInstaller
    {
        public const string ResourceName = "ValheimServerGUI.ServerPlugin.dll";
        public const string FolderName = "ValheimServerGUI";

        /// <summary>The plugin DLL this app carries.</summary>
        public static byte[] PluginBytes()
        {
            using var stream = typeof(ServerPluginInstaller).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException("The server plugin is missing from this build.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        public static string PluginPath(string serverExePath)
            => Path.Join(Path.GetDirectoryName(serverExePath), "BepInEx", "plugins", FolderName, ResourceName);

        public static ServerPluginState Check(string? serverExePath)
        {
            if (string.IsNullOrWhiteSpace(serverExePath) || !File.Exists(serverExePath)) return ServerPluginState.NoServer;

            var bepInEx = Path.Join(Path.GetDirectoryName(serverExePath), "BepInEx", "core", "BepInEx.dll");
            if (!File.Exists(bepInEx)) return ServerPluginState.NoBepInEx;

            var plugin = PluginPath(serverExePath);
            if (!File.Exists(plugin)) return ServerPluginState.NotInstalled;
            return File.ReadAllBytes(plugin).AsSpan().SequenceEqual(PluginBytes()) ? ServerPluginState.Installed : ServerPluginState.Outdated;
        }

        /// <summary>
        /// Copies the plugin into the server's BepInEx plugins folder. Throws <see cref="InvalidOperationException"/>
        /// when the server has no BepInEx, and <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>
        /// when the file cannot be written.
        /// </summary>
        public static void Install(string serverExePath)
        {
            var state = Check(serverExePath);
            if (state is ServerPluginState.NoServer or ServerPluginState.NoBepInEx)
            {
                throw new InvalidOperationException($"The server at {serverExePath} cannot load plugins ({state}).");
            }

            var path = PluginPath(serverExePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, PluginBytes());
        }
    }
}
