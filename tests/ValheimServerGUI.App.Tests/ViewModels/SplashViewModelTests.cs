using Avalonia.Headless.XUnit;
using ValheimServerGUI.App.Startup;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Localization;
using Xunit;

namespace ValheimServerGUI.App.Tests.ViewModels;

public class SplashViewModelTests
{
    [AvaloniaFact]
    public void Steps_fill_the_list_and_the_bar_follows_the_finished_ones()
    {
        var vm = new SplashViewModel();

        vm.Report(new StartupProgress(0, 2, "one", StartupStepState.Pending));
        vm.Report(new StartupProgress(1, 2, "two", StartupStepState.Pending));
        Assert.Equal(2, vm.Steps.Count);
        Assert.True(vm.Steps[0].IsPending);
        Assert.Equal(0, vm.ProgressPercent);

        vm.Report(new StartupProgress(0, 2, "one", StartupStepState.Running));
        Assert.True(vm.Steps[0].IsRunning);
        Assert.Equal(string.Format(Strings.Startup_Running, "one"), vm.StatusText);

        vm.Report(new StartupProgress(0, 2, "one", StartupStepState.Done, "found"));
        Assert.True(vm.Steps[0].IsDone);
        Assert.Equal("found", vm.Steps[0].Detail);
        Assert.Equal(50, vm.ProgressPercent);

        vm.Report(new StartupProgress(1, 2, "two", StartupStepState.Warning, "missing"));
        Assert.True(vm.Steps[1].IsWarning);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.Equal(Strings.Startup_Ready, vm.StatusText);
        Assert.Equal(string.Format(Strings.Startup_Percent, 100.0), vm.PercentText);
    }
}
