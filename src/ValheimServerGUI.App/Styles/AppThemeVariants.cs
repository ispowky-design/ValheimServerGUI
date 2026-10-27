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

    // Rune gold on slate-blue stone, with a warm snow-white text.
    public static readonly Color Gold = Color.Parse("#E2A93B");
    public static readonly Color Snow = Color.Parse("#ECE7DC");

    /// <summary>The Fluent palette (accent, text, chrome) for the Nordic-night look.</summary>
    public static ColorPaletteResources CreateValheimPalette() => new()
    {
        Accent = Gold,
        RegionColor = Color.Parse("#0F1318"),
        ErrorText = Color.Parse("#EE7A62"),

        BaseHigh = Snow,
        BaseMediumHigh = Color.Parse("#CDD3DA"),
        BaseMedium = Color.Parse("#8E9AA8"),
        BaseMediumLow = Color.Parse("#5C6878"),
        BaseLow = Color.Parse("#2A3340"),

        AltHigh = Color.Parse("#0B0E12"),
        AltMediumHigh = Color.Parse("#0E1217"),
        AltMedium = Color.Parse("#11161C"),
        AltMediumLow = Color.Parse("#151B22"),
        AltLow = Color.Parse("#19202A"),

        ChromeAltLow = Color.Parse("#CDD3DA"),
        ChromeBlackHigh = Color.Parse("#05070A"),
        ChromeBlackLow = Color.Parse("#CDD3DA"),
        ChromeBlackMedium = Color.Parse("#0B0E12"),
        ChromeBlackMediumLow = Color.Parse("#0E1217"),
        ChromeDisabledHigh = Color.Parse("#2A3340"),
        ChromeDisabledLow = Color.Parse("#5C6878"),
        ChromeGray = Color.Parse("#7D8896"),
        ChromeHigh = Color.Parse("#3A4656"),
        ChromeLow = Color.Parse("#141920"),
        ChromeMedium = Color.Parse("#1A2129"),
        ChromeMediumLow = Color.Parse("#202833"),
        ChromeWhite = Snow,

        ListLow = Color.Parse("#1A2129"),
        ListMedium = Color.Parse("#3A3220"),
    };
}
