using System;

namespace ValheimServerGUI.Game
{
    /// <summary>A server process that exited while starting or running without being asked to stop.</summary>
    /// <param name="At">When the exit was seen.</param>
    /// <param name="Uptime">How long the server had been running (zero if it crashed while starting).</param>
    /// <param name="ExitCode">The process's exit code, when the OS reported one.</param>
    /// <param name="ConsecutiveCrashes">Crashes in a row, this one included.</param>
    /// <param name="RestartDueAt">When the app starts it again, or null when it will not.</param>
    public sealed record ServerCrash(DateTimeOffset At, TimeSpan Uptime, int? ExitCode, int ConsecutiveCrashes, DateTime? RestartDueAt);
}
