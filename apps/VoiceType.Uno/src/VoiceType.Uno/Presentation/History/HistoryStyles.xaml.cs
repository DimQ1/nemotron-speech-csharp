namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Shared styles of the History screens. Merged as an instance
/// (<c>&lt;history:HistoryStyles /&gt;</c>) rather than by <c>Source</c> URI, which resolves
/// the same way on the WinUI, Skia desktop and Android heads.
/// </summary>
public sealed partial class HistoryStyles : ResourceDictionary
{
    public HistoryStyles() => InitializeComponent();
}
