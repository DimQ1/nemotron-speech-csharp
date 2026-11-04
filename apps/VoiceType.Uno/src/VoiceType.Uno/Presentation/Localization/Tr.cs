using Microsoft.UI.Xaml.Markup;
using VoiceType.Uno.Localization;

namespace VoiceType.Uno.Presentation.Localization;

/// <summary>
/// XAML text in the current interface language: <c>Header="{l:Tr Key=Language.Menu.Header}"</c> with
/// <c>xmlns:l="using:VoiceType.Uno.Presentation.Localization"</c>. Resolved when the page is built; a language change
/// rebuilds the pages (<see cref="LanguageSwitcher"/>), so the value never has to change on a live element.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    /// <summary>The string key (see <see cref="Loc"/>).</summary>
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Loc.T(Key);
}
