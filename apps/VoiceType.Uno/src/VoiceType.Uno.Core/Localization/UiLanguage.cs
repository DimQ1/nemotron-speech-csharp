namespace VoiceType.Uno.Localization;

/// <summary>
/// One interface language of the app. <see cref="Code"/> is what <c>settings.json</c> stores (<c>AppSettings.UiLanguage</c>)
/// and the folder name of its string files (<c>Localization/Strings/&lt;Code&gt;/*.json</c>).
/// </summary>
/// <param name="Code">BCP-47 code of the string set: <c>en</c>, <c>ru</c>, <c>zh-CN</c>, <c>pt-BR</c>, ...</param>
/// <param name="NativeName">The language's own name, shown in the language pickers so everybody finds their language.</param>
/// <param name="EnglishName">English name (logs, tests).</param>
/// <param name="IsRightToLeft">Arabic: the whole window flows right to left.</param>
public sealed record UiLanguage(string Code, string NativeName, string EnglishName, bool IsRightToLeft = false)
{
    /// <summary>What the pickers show.</summary>
    public override string ToString() => NativeName;
}
