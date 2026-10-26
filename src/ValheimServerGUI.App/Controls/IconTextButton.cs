using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ValheimServerGUI.App.Controls;

/// <summary>
/// A command button with a 16×16 <see cref="AppIcons"/> glyph before its <see cref="Text"/> (the dashboard's server
/// and shortcut actions). Keeps the stock Button theme; the glyph greys with the button when it is disabled.
/// </summary>
public class IconTextButton : Button
{
    public static readonly StyledProperty<string?> IconNameProperty =
        AvaloniaProperty.Register<IconTextButton, string?>(nameof(IconName));

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<IconTextButton, string?>(nameof(Text));

    private readonly IconImage _image = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconTextButton()
    {
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _image, _text },
        };
    }

    public string? IconName
    {
        get => GetValue(IconNameProperty);
        set => SetValue(IconNameProperty, value);
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
        else if (change.Property == TextProperty)
        {
            _text.Text = change.GetNewValue<string?>();
        }
    }
}
