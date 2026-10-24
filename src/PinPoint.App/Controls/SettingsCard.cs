using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PinPoint.Controls;

/// <summary>
/// Windows 11 Settings-style row: icon, header, optional description, and a control on the right
/// (the card's Content). Styled implicitly in Styles/Common.xaml.
/// </summary>
public sealed partial class SettingsCard : ContentControl
{
    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty =
        DependencyProperty.Register(nameof(Description), typeof(string), typeof(SettingsCard),
            new PropertyMetadata(string.Empty, (d, _) => ((SettingsCard)d).UpdateDescriptionVisibility()));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata(string.Empty));

    private FrameworkElement? _descriptionPart;

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _descriptionPart = GetTemplateChild("DescriptionText") as FrameworkElement;
        UpdateDescriptionVisibility();
    }

    private void UpdateDescriptionVisibility()
    {
        if (_descriptionPart is not null)
            _descriptionPart.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
    }
}
