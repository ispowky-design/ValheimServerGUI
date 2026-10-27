using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using ValheimServerGUI.App.Services;
using ValheimServerGUI.App.Tests.Services;
using ValheimServerGUI.App.Tests.Fakes;
using ValheimServerGUI.App.ViewModels;
using ValheimServerGUI.Game;
using ValheimServerGUI.Tools;
using ValheimServerGUI.Tools.Logging;
using Xunit;

namespace ValheimServerGUI.App.Tests.Views;

// The custom tab themes pin tab titles to the app font instead of Fluent's SemiLight: the main window's sidebar
// rows read at 13px (Normal, SemiBold when selected), dialog tab strips at the 12px body size.
public class TabFontConsistencyTests
{
    private static readonly System.IServiceProvider Core =
        new ServiceCollection().AddValheimCore().BuildServiceProvider();

    [AvaloniaFact]
    public void Sidebar_rows_use_the_app_font()
    {
        var shell = new ShellLauncher(new RecordingSystemShell(), TestLog.Silent);
        var vm = new MainWindowViewModel(
            Core.GetRequiredService<IServerManager>(), new FakeUserPreferencesProvider(),
            new FakeServerPreferencesProvider(), Core.GetRequiredService<IWorldPreferencesProvider>(),
            Core.GetRequiredService<ISteamCloudWorldProvider>(), Core.GetRequiredService<IIpAddressProvider>(),
            Core.GetRequiredService<IPlayerDataRepository>(), Core.GetRequiredService<IApplicationLogger>(),
            new FakeSoftwareUpdateProvider(), shell, Core.GetRequiredService<IValheimPathResolver>(),
            Core.GetRequiredService<IPlayerListImportService>());
        vm.LoadProfile(new ServerPreferences { ProfileName = "Font" });

        var window = new ValheimServerGUI.App.Views.MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(700, 500));
        window.Arrange(new Rect(new Size(700, 500)));

        var tabs = window.GetVisualDescendants().OfType<TabItem>().ToList();
        Assert.NotEmpty(tabs);
        foreach (var tab in tabs)
        {
            Assert.Equal(13, tab.FontSize);
            Assert.Equal(window.FontFamily, tab.FontFamily);
            Assert.Equal(tab.IsSelected ? FontWeight.SemiBold : FontWeight.Normal, tab.FontWeight);
        }
    }

    // Manage Players merges the same shared tab theme (Styles/Tabs.axaml), so its tabs match too.
    [AvaloniaFact]
    public void Manage_players_tabs_use_the_app_body_font()
    {
        var repo = Core.GetRequiredService<IPlayerDataRepository>();
        var window = new ValheimServerGUI.App.Views.Dialogs.ManagePlayersWindow(
            new ValheimServerGUI.App.ViewModels.Dialogs.ManagePlayersViewModel(new FakeUserPreferencesProvider(), repo));
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var tabs = window.GetVisualDescendants().OfType<TabItem>().ToList();
        Assert.Equal(2, tabs.Count);
        foreach (var tab in tabs)
        {
            Assert.Equal(12, tab.FontSize);
            Assert.Equal(FontWeight.Normal, tab.FontWeight);
        }
    }
}
