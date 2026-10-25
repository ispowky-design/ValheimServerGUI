using System.Diagnostics;

namespace ValheimServerGUI.Tools.Processes
{
    public interface IProcessProvider
    {
        void AddProcess(string key, Process process);

        Process? GetProcess(string key);

        void StartIO(Process process);

        /// <summary>
        /// Gracefully stops the tracked process with the given key, letting it shut down cleanly
        /// (so the Valheim server flushes a world save on the way down). OS-dispatched: Windows sends
        /// a close request via <c>taskkill /pid</c> (no <c>/f</c>); Linux sends SIGINT (never SIGKILL),
        /// exactly as Ctrl+C would. No-op if no live process is tracked under the key.
        /// </summary>
        void SafelyKillProcess(string key);

        /// <summary>
        /// Kills the tracked process with the given key (and its children) immediately. The Valheim server gets no
        /// chance to save, so this is only ever a user's explicit last resort for a stop that will not finish.
        /// No-op if no live process is tracked under the key.
        /// </summary>
        void ForceKillProcess(string key);
    }
}
