using System.Globalization;
using System.Text.RegularExpressions;
using VoiceType.Uno.Localization;
using Xunit;

namespace VoiceType.Uno.Tests.Localization;

/// <summary>
/// The shipped string tables: every language has every English key (plural keys with all categories of the language),
/// no stray keys, the same placeholders, and every key the sources use exists in English.
/// </summary>
public sealed partial class LocalizationCatalogTests
{
    private static readonly IReadOnlyDictionary<string, string> English = Loc.TableOf("en");

    public static TheoryData<string> OtherLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var language in Loc.Languages.Where(l => l.Code != "en"))
            data.Add(language.Code);
        return data;
    }

    [Fact]
    public void Languages_ShouldBeTheSixteenOfTheStoreListing()
    {
        Assert.Equal(
            ["en", "ru", "de", "fr", "es", "zh-CN", "ja", "ko", "pt-BR", "it", "ar", "hi", "tr", "uk", "pl", "nl"],
            Loc.Languages.Select(l => l.Code));
        Assert.All(Loc.Languages, l => Assert.NotNull(CultureInfo.GetCultureInfo(l.Code)));
        Assert.Equal(["ar"], Loc.Languages.Where(l => l.IsRightToLeft).Select(l => l.Code));
    }

    [Fact]
    public void English_ShouldHaveTexts()
    {
        Assert.NotEmpty(English);
        Assert.All(English, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Language_ShouldShipTheSameFilesAsEnglish(string code)
    {
        static IEnumerable<string> FileNames(string c) =>
            Loc.ResourceFiles(c).Select(n => n[(n.LastIndexOf('/') + 1)..]).Order(StringComparer.Ordinal);

        Assert.Equal(FileNames("en"), FileNames(code));
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Language_ShouldTranslateEveryKey(string code)
    {
        var table = Loc.TableOf(code);
        var missing = ExpectedKeys(code).Where(k => !table.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, $"{code}: {missing.Count} missing, e.g. {string.Join(", ", missing.Take(15))}");
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Language_ShouldHaveNoStrayKeys(string code)
    {
        var expected = ExpectedKeys(code).ToHashSet(StringComparer.Ordinal);
        var stray = Loc.TableOf(code).Keys.Where(k => !expected.Contains(k) && !IsOptionalPluralForm(k)).ToList();
        Assert.True(stray.Count == 0, $"{code}: {stray.Count} stray, e.g. {string.Join(", ", stray.Take(15))}");
    }

    [Theory]
    [MemberData(nameof(OtherLanguages))]
    public void Language_ShouldKeepThePlaceholders(string code)
    {
        var bad = new List<string>();
        foreach (var (key, text) in Loc.TableOf(code))
        {
            var source = EnglishSourceOf(key);
            if (source is null)
                continue;
            var expected = Placeholders(source);
            var actual = Placeholders(text);
            // A plural form for exactly one item may spell the number out ("одна запись"); every other text keeps them all.
            var ok = PluralCategory(key) is PluralRules.One or PluralRules.Zero or PluralRules.Two
                ? actual.IsSubsetOf(expected)
                : actual.SetEquals(expected);
            if (!ok || !IsValidFormat(text))
                bad.Add($"{key}: '{text}'");
        }
        Assert.True(bad.Count == 0, $"{code}: {bad.Count} bad, e.g. {string.Join(" | ", bad.Take(10))}");
    }

    [Fact]
    public void English_ShouldBeValidFormats()
    {
        var bad = English.Where(kv => !IsValidFormat(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void English_PluralKeys_ShouldHaveOneAndOther()
    {
        var bad = English.Keys.Where(k => PluralCategory(k) is not null)
            .Select(k => k[..k.LastIndexOf('.')]).Distinct()
            .Where(b => !English.ContainsKey(b + ".one") || !English.ContainsKey(b + ".other")).ToList();
        Assert.Empty(bad);
    }

    [Fact]
    public void Sources_ShouldOnlyUseKeysThatExist()
    {
        var root = SourceRoot();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                                 && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match m in CodeKey().Matches(text))
            {
                var key = m.Groups["key"].Value;
                var plural = m.Groups["fn"].Value == "Plural";
                if (plural ? !English.ContainsKey(key + ".other") : !English.ContainsKey(key))
                    missing.Add($"{key} ({Path.GetFileName(file)})");
            }
            foreach (Match m in XamlKey().Matches(text))
            {
                if (!English.ContainsKey(m.Groups["key"].Value))
                    missing.Add($"{m.Groups["key"].Value} ({Path.GetFileName(file)})");
            }
        }
        Assert.True(missing.Count == 0, $"{missing.Count} keys used but not in en: {string.Join(", ", missing.Take(30))}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>The keys a language must have: English keys, plural ones expanded to the language's categories.</summary>
    private static IEnumerable<string> ExpectedKeys(string code)
    {
        var plurals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in English.Keys)
        {
            if (PluralCategory(key) is not null)
                plurals.Add(key[..key.LastIndexOf('.')]);
            else
                yield return key;
        }
        foreach (var basis in plurals)
        foreach (var category in PluralRules.CategoriesOf(code))
            yield return basis + "." + category;
    }

    private static bool IsOptionalPluralForm(string key) => PluralCategory(key) is not null
        && English.Keys.Any(k => k.StartsWith(key[..key.LastIndexOf('.')] + ".", StringComparison.Ordinal) && PluralCategory(k) is not null);

    private static string? EnglishSourceOf(string key)
    {
        if (English.TryGetValue(key, out var text))
            return text;
        if (PluralCategory(key) is not null && English.TryGetValue(key[..key.LastIndexOf('.')] + ".other", out var other))
            return other;
        return null;
    }

    private static string? PluralCategory(string key)
    {
        var dot = key.LastIndexOf('.');
        if (dot < 0) return null;
        var tail = key[(dot + 1)..];
        return tail is PluralRules.Zero or PluralRules.One or PluralRules.Two or PluralRules.Few or PluralRules.Many or PluralRules.Other
            ? tail : null;
    }

    private static HashSet<int> Placeholders(string text) =>
        PlaceholderRegex().Matches(text.Replace("{{", "").Replace("}}", "")).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToHashSet();

    private static bool IsValidFormat(string text)
    {
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, text, Enumerable.Range(0, 10).Select(i => (object)i).ToArray());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string SourceRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "apps", "VoiceType.Uno", "src");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException("apps/VoiceType.Uno/src not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\bLoc\.(?<fn>T|F|Plural|In)\(\s*(?:[^,()""]+,\s*)?""(?<key>[A-Za-z0-9_.]+)""")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{l:Tr\s+Key=(?<key>[A-Za-z0-9_.]+)\s*\}")]
    private static partial Regex XamlKey();
}
