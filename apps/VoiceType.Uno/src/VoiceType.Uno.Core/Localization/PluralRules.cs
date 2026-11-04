namespace VoiceType.Uno.Localization;

/// <summary>
/// CLDR cardinal plural categories for the interface languages (integers only: the app counts recordings, files, speakers).
/// A plural string is stored as several keys, <c>&lt;key&gt;.one</c>, <c>.few</c>, <c>.many</c>, <c>.other</c> (Arabic also
/// <c>.zero</c> and <c>.two</c>); <c>.other</c> is always present.
/// </summary>
public static class PluralRules
{
    public const string Zero = "zero";
    public const string One = "one";
    public const string Two = "two";
    public const string Few = "few";
    public const string Many = "many";
    public const string Other = "other";

    /// <summary>The category of <paramref name="count"/> in <paramref name="languageCode"/> (only the primary subtag matters).</summary>
    public static string Category(string languageCode, long count)
    {
        var n = Math.Abs(count);
        var lang = Primary(languageCode);
        switch (lang)
        {
            case "zh":
            case "ja":
            case "ko":
                return Other;
            case "fr":
            case "hi":
                return n is 0 or 1 ? One : Other;
            case "pt" when languageCode.Equals("pt-PT", StringComparison.OrdinalIgnoreCase):
                return n == 1 ? One : Other;
            case "pt":
                return n is 0 or 1 ? One : Other;
            case "ru":
            case "uk":
            {
                var mod10 = n % 10;
                var mod100 = n % 100;
                if (mod10 == 1 && mod100 != 11) return One;
                if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return Few;
                return Many;
            }
            case "pl":
            {
                if (n == 1) return One;
                var mod10 = n % 10;
                var mod100 = n % 100;
                if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14) return Few;
                return Many;
            }
            case "ar":
            {
                if (n == 0) return Zero;
                if (n == 1) return One;
                if (n == 2) return Two;
                var mod100 = n % 100;
                if (mod100 is >= 3 and <= 10) return Few;
                if (mod100 is >= 11 and <= 99) return Many;
                return Other;
            }
            default: // en, de, nl, it, es, tr and anything unknown
                return n == 1 ? One : Other;
        }
    }

    /// <summary>The categories a language uses, <see cref="Other"/> last (tests check every plural key has them all).</summary>
    public static IReadOnlyList<string> CategoriesOf(string languageCode) => Primary(languageCode) switch
    {
        "zh" or "ja" or "ko" => [Other],
        "ru" or "uk" or "pl" => [One, Few, Many, Other],
        "ar" => [Zero, One, Two, Few, Many, Other],
        _ => [One, Other],
    };

    private static string Primary(string code)
    {
        var dash = code.IndexOf('-');
        return (dash < 0 ? code : code[..dash]).ToLowerInvariant();
    }
}
