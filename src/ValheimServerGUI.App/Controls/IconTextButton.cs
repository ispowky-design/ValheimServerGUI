using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ValheimServerGUI.App.Controls;

/// <summary>
/// A command button with a glyph before its <see cref="Text"/> (the top bar's server actions, the dashboard's
/// shortcuts): either a 16×16 <see cref="AppIcons"/> bitmap (<see cref="IconName"/>) or a vector <see cref="Glyph"/>
/// from Styles/NavIcons.axaml, drawn in the button's text colour. Keeps the stock Button theme; the glyph greys with
/// the button when it is disabled.
/// </summary>
public class IconTextButton : Button
{
    public static readonly StyledProperty<string?> IconNameProperty =
        AvaloniaProperty.Register<IconTextButton, string?>(nameof(IconName));

    public static readonly StyledProperty<Geometry?> GlyphProperty =
        AvaloniaProperty.Register<IconTextButton, Geometry?>(nameof(Glyph));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconTextButton, string?>(nameof(Text));

    private readonly IconImage _image = new() { VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly PathIcon _glyph = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconTextButton()
    {
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _image, _glyph, _text },
        };
    }

    public string? IconName
    {
        get => GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
    }

    public Geometry? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override Type StyleKeyOverride => typeof(Button);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconNameProperty)
        {
            _image.IconName = change.GetNewValue<string?>();
            _image.IsVisible = !string.IsNullOrEmpty(_image.IconName);
        }
        else if (change.Property == GlyphProperty)
        {
            _glyph.Data = change.GetNewValue<Geometry?>();
            _glyph.IsVisible = _glyph.Data is not null;
        }
        else if (change.Property == TextProperty)
        {
            _text.Text = change.GetNewValue<string?>();
        }
    }
}
