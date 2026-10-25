using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Linq;
using ValheimServerGUI.App.Styles;
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

// The layered surface palette resolves in both theme variants, the three layers are distinct, and none is
// pure black (the app uses no solid black anywhere). The window paints the base layer. Covers the Valheim theme too.
public class PaletteTests
{
    private static readonly System.IServiceProvider Core =
        new ServiceCollection().AddValheimCore().BuildServiceProvider();

    private static readonly Color Black = Color.FromArgb(0xFF, 0, 0, 0);

    private static Color Resolve(Window w, string key)
    {
        Assert.True(w.TryFindResource(key, w.ActualThemeVariant, out var v), $"{key} did not resolve");
        return ((ISolidColorBrush)v!).Color;
    }

    private static Window ShowWindow()
    {
        var shell = new ShellLauncher(new RecordingSystemShell(), TestLog.Silent);
        var vm = new MainWindowViewModel(
            Core.GetRequiredService<IServerManager>(), new FakeUserPreferencesProvider(),
            new FakeServerPreferencesProvider(), Core.GetRequiredService<IWorldPreferencesProvider>(),
            Core.GetRequiredService<ISteamCloudWorldProvider>(), Core.GetRequiredService<IIpAddressProvider>(),
            Core.GetRequiredService<IPlayerDataRepository>(), Core.GetRequiredService<IApplicationLogger>(),
            new FakeSoftwareUpdateProvider(), shell, Core.GetRequiredService<IValheimPathResolver>(),
            Core.GetRequiredService<IPlayerListImportService>());
        var window = new ValheimServerGUI.App.Views.MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ThemeVariant Variant(string name) => name switch
    {
        "Dark" => ThemeVariant.Dark,
        "Valheim" => AppThemeVariants.Valheim,
        _ => ThemeVariant.Light,
    };

    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Valheim")]
    public void Layers_are_distinct_and_never_pure_black(string variantName)
    {
        var window = ShowWindow();
        window.RequestedThemeVariant = Variant(variantName);
        Dispatcher.UIThread.RunJobs();

        var darkest = Resolve(window, "LayerDarkest");
        var baseColor = Resolve(window, "LayerBase");
        var content = Resolve(window, "LayerContent");

        Assert.NotEqual(Black, darkest);
        Assert.NotEqual(Black, baseColor);
        Assert.NotEqual(Black, content);
        Assert.NotEqual(darkest, baseColor);
        Assert.NotEqual(baseColor, content);

        // App background blends with the menu/footer (darkest layer).
        Assert.Equal(darkest, (window.Background as ISolidColorBrush)!.Color);
    }

    // The Valheim theme has its own oak layers (not Dark's greys), and every theme defines the help glyph.
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    [InlineData("Valheim")]
    public void Each_theme_resolves_its_own_layers_and_help_glyph(string variantName)
    {
        var window = ShowWindow();
        window.RequestedThemeVariant = ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        var darkBase = Resolve(window, "LayerBase");

        window.RequestedThemeVariant = Variant(variantName);
        Dispatcher.UIThread.RunJobs();

        Resolve(window, "HelpGlyphBrush");
        if (variantName == "Valheim") Assert.NotEqual(darkBase, Resolve(window, "LayerBase"));
    }

    // Choosing Valheim installs the wood-and-gold Fluent palette (gold accent); any other theme takes it out again,
    // so plain Dark keeps Fluent's own accent.
    [AvaloniaFact]
    public void Apply_theme_installs_the_gold_palette_only_for_Valheim()
    {
        var app = ValheimServerGUI.App.App.Instance;
        var fluent = app.Styles.OfType<Avalonia.Themes.Fluent.FluentTheme>().Single();
        try
        {
            app.ApplyTheme(AppTheme.Valheim);
            Assert.Equal(AppThemeVariants.Valheim, app.RequestedThemeVariant);
            Assert.Equal(AppThemeVariants.Gold, fluent.Palettes[ThemeVariant.Dark].Accent);

            app.ApplyTheme(AppTheme.Dark);
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
            Assert.False(fluent.Palettes.ContainsKey(ThemeVariant.Dark));
        }
        finally
        {
            app.ApplyTheme(AppTheme.System);
        }
    }
}
