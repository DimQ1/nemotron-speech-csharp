using System.Text.RegularExpressions;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

public sealed record ClarifyCandidate(int Index, string Label, WindowTarget Target);

public sealed class ClarifyTargetViewModel
{
    private static readonly IReadOnlyDictionary<string, int> SpokenNumbers =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1, ["one"] = 1, ["первое"] = 1, ["первый"] = 1, ["первая"] = 1, ["раз"] = 1,
            ["second"] = 2, ["two"] = 2, ["второе"] = 2, ["второй"] = 2, ["вторая"] = 2, ["два"] = 2,
            ["third"] = 3, ["three"] = 3, ["третье"] = 3, ["третий"] = 3, ["третья"] = 3, ["три"] = 3,
            ["fourth"] = 4, ["four"] = 4, ["четвёртое"] = 4, ["четвертое"] = 4,
            ["четвёртый"] = 4, ["четвертый"] = 4, ["четыре"] = 4,
            ["fifth"] = 5, ["five"] = 5, ["пятое"] = 5, ["пятый"] = 5, ["пять"] = 5,
            ["sixth"] = 6, ["six"] = 6, ["шестое"] = 6, ["шестой"] = 6, ["шесть"] = 6,
            ["seventh"] = 7, ["seven"] = 7, ["седьмое"] = 7, ["седьмой"] = 7, ["семь"] = 7,
            ["eighth"] = 8, ["eight"] = 8, ["восьмое"] = 8, ["восьмой"] = 8, ["восемь"] = 8,
            ["ninth"] = 9, ["nine"] = 9, ["девятое"] = 9, ["девятый"] = 9, ["девять"] = 9,
            ["tenth"] = 10, ["ten"] = 10, ["десятое"] = 10, ["десятый"] = 10, ["десять"] = 10,
        };

    public ClarifyTargetViewModel(string appName, IReadOnlyList<WindowTarget> candidates)
    {
        Prompt = $"Type into which field of \"{appName}\"?";
        Candidates = candidates
            .Select((target, index) => new ClarifyCandidate(index + 1, target.DisplayLabel, target))
            .ToArray();
    }

    public string Prompt { get; }

    public IReadOnlyList<ClarifyCandidate> Candidates { get; }

    public WindowTarget? SelectedTarget { get; private set; }

    public bool TrySelectByVoice(string text)
    {
        if (!TryParseSelectionNumber(text, out var number))
            return false;

        return TrySelectByIndex(number);
    }

    public bool TrySelectByIndex(int number)
    {
        if (number < 1 || number > Candidates.Count)
            return false;

        SelectedTarget = Candidates[number - 1].Target;
        return true;
    }

    private static bool TryParseSelectionNumber(string text, out int number)
    {
        number = 0;
        var normalized = text.Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            return false;

        var digitMatch = Regex.Match(normalized, @"(\d+)\s*[.,!?;:]*$");
        if (digitMatch.Success && int.TryParse(digitMatch.Groups[1].Value, out number))
            return true;

        var lastWord = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?
            .Trim('.', ',', '!', '?', ';', ':');
        return lastWord is not null && SpokenNumbers.TryGetValue(lastWord, out number);
    }
}