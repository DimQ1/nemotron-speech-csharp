using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.History;

namespace VoiceType.Uno.Presentation.Controls;

/// <summary>
/// A speaker colour (palette index 0..7) as a dot, bar or button face. Follows the theme: the palette has
/// a darker tone for light and a lighter tone for dark. An index below 0 is transparent (no speaker).
/// A user control around a <see cref="Border"/> (WinUI's Border is sealed); Width, Height and CornerRadius apply as usual.
/// </summary>
public sealed partial class SpeakerSwatch : UserControl
{
    public static readonly DependencyProperty ColorIndexProperty = DependencyProperty.Register(
        nameof(ColorIndex), typeof(int), typeof(SpeakerSwatch),
        new PropertyMetadata(-1, (d, _) => ((SpeakerSwatch)d).Apply()));

    private readonly Border _border = new();

    public SpeakerSwatch()
    {
        IsTabStop = false;
        Content = _border;
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => _border.CornerRadius = CornerRadius);
        Loaded += (_, _) => Apply();
        ActualThemeChanged += (_, _) => Apply();
    }

    public int ColorIndex
    {
        get => (int)GetValue(ColorIndexProperty);
        set => SetValue(ColorIndexProperty, value);
    }

    private void Apply()
    {
        _border.CornerRadius = CornerRadius;
        _border.Background = ColorIndex < 0 ? null : SpeakerServices.BrushFor(ColorIndex, this);
    }

    /// <summary>Accessible name of a colour: "Blue".</summary>
    public static string NameOf(int index) => SpeakersUi.ColorName(index);
}
