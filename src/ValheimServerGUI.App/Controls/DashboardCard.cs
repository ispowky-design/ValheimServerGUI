using Avalonia;
using Avalonia.Controls;

namespace ValheimServerGUI.App.Controls;

/// <summary>
/// A dashboard panel: a small <see cref="Title"/> row (with an optional <see cref="AppIcons"/> glyph and
/// <see cref="HeaderContent"/> on the right, such as a link), then an optional large <see cref="Value"/> with a muted
/// <see cref="Caption"/> under it, then the <see cref="ContentControl.Content"/> (a <see cref="Sparkline"/>, a list,
/// a log). Used both as a stat tile (Value + Caption) and as a titled section (Content only). The look lives in
/// <c>DashboardCard.axaml</c>.
/// </summary>
public class DashboardCard : ContentControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DashboardCard, string?>(nameof(Title));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<DashboardCard, string?>(nameof(Value));

    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<DashboardCard, string?>(nameof(Caption));

    public static readonly StyledProperty<string?> IconNameProperty =
        AvaloniaProperty.Register<DashboardCard, string?>(nameof(IconName));

    public static readonly StyledProperty<object?> HeaderContentProperty =
        AvaloniaProperty.Register<DashboardCard, object?>(nameof(HeaderContent));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string? IconName
    {
        get => GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public object? HeaderContent
    {
        get => GetValue(HeaderContentProperty);
        set => SetValue(HeaderContentProperty, value);
    }
}
