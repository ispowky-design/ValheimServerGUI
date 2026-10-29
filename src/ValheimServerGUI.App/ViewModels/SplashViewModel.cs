using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ValheimServerGUI.App.Startup;
using ValheimServerGUI.Localization;
using ValheimServerGUI.Tools;

namespace ValheimServerGUI.App.ViewModels;

/// <summary>
/// Backs the splash window: the list of startup steps with each one's state and finding, a determinate progress bar
/// (0–100) over the finished steps, and a status line naming the step under way.
/// </summary>
public partial class SplashViewModel : ViewModelBase, IProgress<StartupProgress>
{
    /// <summary>App version shown under the name on the splash (e.g. "v3.0.0-rc.1").</summary>
    public string Version { get; } = "v" + AssemblyHelper.GetApplicationVersion();

    /// <summary>One row per startup step, in run order.</summary>
    public ObservableCollection<SplashStepViewModel> Steps { get; } = new();

    [ObservableProperty]
    private string _statusText = Strings.Startup_Starting;

    /// <summary>Progress as a percentage (0–100) for a determinate <c>ProgressBar</c>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _progressPercent;

    public string PercentText => string.Format(Strings.Startup_Percent, ProgressPercent);

    public void Report(StartupProgress value) => RunOnUi(() =>
    {
        while (Steps.Count <= value.Index)
            Steps.Add(new SplashStepViewModel());

        var step = Steps[value.Index];
        step.Title = value.Title;
        step.State = value.State;
        step.Detail = value.Detail;

        ProgressPercent = value.Count == 0 ? 100 : 100.0 * Steps.Count(s => s.IsFinished) / value.Count;
        if (value.State == StartupStepState.Running)
            StatusText = string.Format(Strings.Startup_Running, value.Title);
        else if (Steps.Count == value.Count && Steps.All(s => s.IsFinished))
            StatusText = Strings.Startup_Ready;
    });

    /// <summary>The steps are done; the main window is being built.</summary>
    public void ReportOpening() => RunOnUi(() =>
    {
        ProgressPercent = 100;
        StatusText = Strings.Startup_Opening;
    });
}

/// <summary>One startup step's line on the splash screen.</summary>
public partial class SplashStepViewModel : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _detail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsRunning), nameof(IsDone), nameof(IsWarning), nameof(IsSkipped), nameof(IsFinished))]
    private StartupStepState _state;

    public bool IsPending => State == StartupStepState.Pending;
    public bool IsRunning => State == StartupStepState.Running;
    public bool IsDone => State == StartupStepState.Done;
    public bool IsWarning => State == StartupStepState.Warning;
    public bool IsSkipped => State == StartupStepState.Skipped;
    public bool IsFinished => State is StartupStepState.Done or StartupStepState.Warning or StartupStepState.Skipped;
}
