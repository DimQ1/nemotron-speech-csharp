using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.Controls;

/// <summary>
/// The icon of an audio source on the main screen chip: a microphone, a speaker (what the computer plays), or both side by
/// side. The symbol set has no single "microphone + speaker" icon and its <c>Audio</c> symbol is a musical note.
/// </summary>
internal static class AudioSourceIcons
{
    /// <summary>One side of the pair: a 16 px icon, so two of them take about as much room as a 20 px icon and a half.</summary>
    private const double PairIconSize = 16;

    public static UIElement Create(AudioSourceGlyph glyph) => glyph switch
    {
        AudioSourceGlyph.SystemAudio => new SymbolIcon(Symbol.Volume),
        AudioSourceGlyph.Both => CreatePair(),
        _ => new SymbolIcon(Symbol.Microphone),
    };

    private static UIElement CreatePair() => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 1,
        Children = { Small(Symbol.Microphone), Small(Symbol.Volume) },
    };

    private static UIElement Small(Symbol symbol) => new Viewbox
    {
        Width = PairIconSize,
        Height = PairIconSize,
        Child = new SymbolIcon(symbol),
    };
}
