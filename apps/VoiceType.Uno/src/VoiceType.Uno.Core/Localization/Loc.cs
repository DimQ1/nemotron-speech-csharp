using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace VoiceType.Uno.Localization;

/// <summary>
/// The interface strings of the app in the current language, switched at run time without a restart.
/// <para>
/// Strings live in <c>Localization/Strings/&lt;code&gt;/&lt;area&gt;.json</c> of this assembly (embedded): flat objects of
/// <c>"Area.Name": "text"</c>. English (<c>en</c>) is the source and complete; a key missing in another language falls back
/// to English, a key missing in English shows the key itself (a test fails on both). Format strings use
/// <see cref="string.Format(IFormatProvider, string, object[])"/> placeholders <c>{0}</c>, <c>{1:N0}</c>; plural strings are
/// several keys, see <see cref="Plural"/>.
/// </para>
/// <para>
/// <see cref="SetLanguage"/> swaps the table and raises <see cref="LanguageChanged"/>; the UI rebuilds its pages and the
/// long-lived view models re-compute their texts. Starts in English, so unit tests see the English texts.
/// </para>
/// </summary>
public static class Loc
{
    public const string DefaultCode = "en";

    /// <summary>The interface languages, in the order of the Store listing (docs/store/imports).</summary>
    public static IReadOnlyList<UiLanguage> Languages { get; } =
    [
        new("en", "English", "English"),
        new("ru", "Русский", "Russian"),
        new("de", "Deutsch", "German"),
        new("fr", "Français", "French"),
        new("es", "Español", "Spanish"),
        new("zh-CN", "简体中文", "Chinese (Simplified)"),
        new("ja", "日本語", "Japanese"),
        new("ko", "한국어", "Korean"),
        new("pt-BR", "Português (Brasil)", "Portuguese (Brazil)"),
        new("it", "Italiano", "Italian"),
        new("ar", "العربية", "Arabic", IsRightToLeft: true),
        new("hi", "हिन्दी", "Hindi"),
        new("tr", "Türkçe", "Turkish"),
        new("uk", "Українська", "Ukrainian"),
        new("pl", "Polski", "Polish"),
        new("nl", "Nederlands", "Dutch"),
    ];

    private static readonly object Gate = new();
    private static readonly Dictionary<string, FrozenDictionary<string, string>> Tables = new(StringComparer.OrdinalIgnoreCase);
    private static FrozenDictionary<string, string> _english = Load(DefaultCode);
    private static FrozenDictionary<string, string> _current = _english;
    private static UiLanguage _language = Languages[0];

    /// <summary>Raised (on the caller's thread) after <see cref="SetLanguage"/> switched to another language.</summary>
    public static event Action? LanguageChanged;

    /// <summary>The current interface language.</summary>
    public static UiLanguage Current => _language;

    /// <summary>Culture of the current language, for numbers and dates inside interface texts.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    /// <summary>The language of <paramref name="code"/>, or null when the app has no such interface language.</summary>
    public static UiLanguage? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        return Languages.FirstOrDefault(l => l.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The interface language closest to a system culture: an exact match (<c>pt-BR</c>), then the same primary language
    /// (<c>pt-PT</c> → <c>pt-BR</c>, <c>zh-TW</c> → <c>zh-CN</c>, <c>de-AT</c> → <c>de</c>), else English.
    /// Preselects the first-run question.
    /// </summary>
    public static UiLanguage MatchSystem(CultureInfo culture)
    {
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
        {
            if (Find(c.Name) is { } exact) return exact;
            var primary = c.TwoLetterISOLanguageName;
            var same = Languages.FirstOrDefault(l => l.Code.Split('-')[0].Equals(primary, StringComparison.OrdinalIgnoreCase));
            if (same is not null) return same;
            if (ReferenceEquals(c, c.Parent)) break;
        }
        return Languages[0];
    }

    /// <summary>
    /// Switches the interface language (unknown codes → English). Sets the UI culture of the process too, so
    /// platform controls (date pickers, the text box context menu) follow. Returns true when the language changed.
    /// </summary>
    public static bool SetLanguage(string? code)
    {
        var language = Find(code) ?? Languages[0];
        lock (Gate)
        {
            if (language == _language) return false;
            _current = language.Code == DefaultCode ? _english : Table(language.Code);
            _language = language;
            Culture = CultureInfo.GetCultureInfo(language.Code);
        }
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
        LanguageChanged?.Invoke();
        return true;
    }

    /// <summary>The text of <paramref name="key"/> in the current language.</summary>
    public static string T(string key) =>
        _current.TryGetValue(key, out var text) || _english.TryGetValue(key, out text) ? text : key;

    /// <summary>The text of <paramref name="key"/> in another language (the first-run question previews the choice).</summary>
    public static string In(string code, string key) =>
        TableOf(Find(code)?.Code ?? DefaultCode).TryGetValue(key, out var text) || _english.TryGetValue(key, out text) ? text : key;

    /// <summary>The text of <paramref name="key"/> formatted with <paramref name="args"/> in the current culture.</summary>
    public static string F(string key, params object?[] args)
    {
        var format = T(key);
        try
        {
            return string.Format(Culture, format, args);
        }
        catch (FormatException)
        {
            // A broken translation never takes the page down: fall back to the English format.
            return _english.TryGetValue(key, out var en) ? string.Format(Culture, en, args) : format;
        }
    }

    /// <summary>
    /// A count phrase: picks <c>key.one</c>, <c>key.few</c>, ... by the plural rules of the current language
    /// (<see cref="PluralRules"/>), falls back to <c>key.other</c>, and formats it with <paramref name="count"/> as
    /// <c>{0}</c> and <paramref name="args"/> as <c>{1}</c>, <c>{2}</c>, ...
    /// </summary>
    public static string Plural(string key, long count, params object?[] args)
    {
        var category = PluralRules.Category(_language.Code, count);
        var full = key + "." + category;
        var chosen = _current.ContainsKey(full) ? full
            : _current.ContainsKey(key + ".other") ? key + ".other"
            : _english.ContainsKey(key + "." + PluralRules.Category(DefaultCode, count)) ? key + "." + PluralRules.Category(DefaultCode, count)
            : key + ".other";
        var all = new object?[args.Length + 1];
        all[0] = count;
        Array.Copy(args, 0, all, 1, args.Length);
        return F(chosen, all);
    }

    /// <summary>All keys and texts of one language as shipped (tests: completeness, placeholders).</summary>
    public static IReadOnlyDictionary<string, string> TableOf(string code) =>
        code.Equals(DefaultCode, StringComparison.OrdinalIgnoreCase) ? _english : Table(code);

    private static FrozenDictionary<string, string> Table(string code)
    {
        lock (Gate)
        {
            if (!Tables.TryGetValue(code, out var table))
                Tables[code] = table = Load(code);
            return table;
        }
    }

    private const string ResourcePrefix = "loc/";

    private static FrozenDictionary<string, string> Load(string code)
    {
        var assembly = typeof(Loc).Assembly;
        var prefix = ResourcePrefix + code + "/";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            var normalized = name.Replace('\\', '/');
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    result[property.Name] = property.Value.GetString()!;
            }
        }
        return result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Names of the embedded string files of a language (tests).</summary>
    internal static IEnumerable<string> ResourceFiles(string code) =>
        typeof(Loc).Assembly.GetManifestResourceNames()
            .Select(n => n.Replace('\\', '/'))
            .Where(n => n.StartsWith(ResourcePrefix + code + "/", StringComparison.OrdinalIgnoreCase));

    internal static Assembly ResourceAssembly => typeof(Loc).Assembly;
}
