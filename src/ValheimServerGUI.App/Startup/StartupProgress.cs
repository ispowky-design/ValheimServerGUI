namespace ValheimServerGUI.App.Startup;

/// <summary>Where one startup step stands, as the splash screen shows it.</summary>
public enum StartupStepState
{
    Pending,
    Running,

    /// <summary>Finished as expected.</summary>
    Done,

    /// <summary>Finished, but found something the user should look at (no server, plugin out of date, a failure…).</summary>
    Warning,

    /// <summary>Nothing to do here (feature not set up or turned off).</summary>
    Skipped,
}

/// <summary>Progress update reported by <see cref="StartupService"/> to the splash view: one step's new state.</summary>
/// <param name="Index">The step's position in the startup sequence, from 0.</param>
/// <param name="Count">How many steps the startup sequence has.</param>
/// <param name="Title">The step's name (what is being loaded).</param>
/// <param name="State">Where the step stands.</param>
/// <param name="Detail">What the step found, shown beside its name once it has run.</param>
public readonly record struct StartupProgress(int Index, int Count, string Title, StartupStepState State, string? Detail = null)
{
    /// <summary>True once the step has run, whatever it found.</summary>
    public bool IsFinished => State is StartupStepState.Done or StartupStepState.Warning or StartupStepState.Skipped;
}
