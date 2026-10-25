using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ValheimServerGUI.App.Styles;

/// <summary>
/// The app's own theme variants beyond Light/Dark. <see cref="Valheim"/> inherits every Dark resource; the layer
/// and input tokens it overrides live in Palette.axaml, and the Fluent colors come from
/// <see cref="CreateValheimPalette"/>, which <c>App.ApplyTheme</c> installs while the theme is active.
/// </summary>
public static class AppThemeVariants
{
    public static readonly ThemeVariant Valheim = new("Valheim", ThemeVariant.Dark);

    // Gold accent on dark oak, with parchment text.
    public static readonly Color Gold = Color.Parse("#D4A63A");
    public static readonly Color Parchment = Color.Parse("#F2E4C4");

    /// <summary>The Fluent palette (accent, text, chrome) for the wood-and-gold look.</summary>
    public static ColorPaletteResources CreateValheimPalette() => new()
    {
        Accent = Gold,
        RegionColor = Color.Parse("#2B1F15"),
        ErrorText = Color.Parse("#E5735C"),

        BaseHigh = Parchment,
        BaseMediumHigh = Color.Parse("#E0CFA8"),
        BaseMedium = Color.Parse("#B9A27C"),
        BaseMediumLow = Color.Parse("#8C7656"),
        BaseLow = Color.Parse("#5A4632"),

        AltHigh = Color.Parse("#1C140D"),
        AltMediumHigh = Color.Parse("#241A11"),
        AltMedium = Color.Parse("#2B1F15"),
        AltMediumLow = Color.Parse("#33251A"),
        AltLow = Color.Parse("#3A2B1E"),

        ChromeAltLow = Color.Parse("#E0CFA8"),
        ChromeBlackHigh = Color.Parse("#0E0A06"),
        ChromeBlackLow = Color.Parse("#E0CFA8"),
        ChromeBlackMedium = Color.Parse("#1C140D"),
        ChromeBlackMediumLow = Color.Parse("#241A11"),
        ChromeDisabledHigh = Color.Parse("#5A4632"),
        ChromeDisabledLow = Color.Parse("#8C7656"),
        ChromeGray = Color.Parse("#A38D6A"),
        ChromeHigh = Color.Parse("#7A6146"),
        ChromeLow = Color.Parse("#2F2217"),
        ChromeMedium = Color.Parse("#3A2B1E"),
        ChromeMediumLow = Color.Parse("#463424"),
        ChromeWhite = Parchment,

        ListLow = Color.Parse("#3F2E1F"),
        ListMedium = Color.Parse("#5C4428"),
    };
}
