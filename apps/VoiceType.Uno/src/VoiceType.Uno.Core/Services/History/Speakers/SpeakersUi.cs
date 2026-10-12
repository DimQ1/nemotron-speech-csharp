using System.Globalization;

namespace VoiceType.Uno.History;

/// <summary>How the speaker controls above a transcript are laid out (design §10.8).</summary>
public enum SpeakerBarMode
{
    /// <summary>Below 600 px: a one-line summary that opens the Speakers sheet.</summary>
    Summary,

    /// <summary>600–839 px or a low window: an inline picker (filter) next to the sheet button.</summary>
    Picker,

    /// <summary>From 840 × 480: one filter chip per speaker.</summary>
    Chips,
}

/// <summary>A run of segment text; <see cref="ColorIndex"/> null = default text colour.</summary>
public sealed record SpeakerTextRun(string Text, int? ColorIndex, bool IsMarker);

/// <summary>One transcript segment as the transcript list draws it.</summary>
/// <param name="Position">Index in the version's segment list.</param>
/// <param name="PersonId">Majority speaker, null when the layer has no label for it.</param>
/// <param name="ShowHeader">First segment of a run by one speaker: the name line is drawn above it.</param>
/// <param name="IsHidden">Another speaker's segment while a filter is active.</param>
/// <param name="CollapsedText">Set on the first hidden segment of a run: <c>··· 3 segments by Mark</c>.</param>
public sealed record SpeakerRowInfo(int Position, string? PersonId, bool ShowHeader, bool IsHidden, string? CollapsedText);

/// <summary>
/// Pure logic of the speaker UI: palette, summary / filter wording, grouping of segments into
/// speaker runs, filtering and turn navigation. No UI types, so it is unit-tested.
/// </summary>
public static class SpeakersUi
{
    public const int PaletteSize = 8;

    /// <summary>Segments by someone who talks less than this share of the recording trigger the "merge?" hint (design §10.6).</summary>
    public const double MinorSpeakerPercent = 3.0;

    public const int MaxVoices = 8;

    // ARGB. Light values are dark enough for text on white (≥ 4.5:1); dark values are their light counterparts.
    private static readonly uint[] LightColors =
    [
        0xFF0F6CBD, // blue
        0xFFC4314B, // rose
        0xFF0E7A5F, // green
        0xFF9A6400, // amber
        0xFF7B3FBF, // violet
        0xFFC2410C, // orange
        0xFF1B7F8F, // teal
        0xFFB0307A, // magenta
    ];

    private static readonly uint[] DarkColors =
    [
        0xFF6CB8FF,
        0xFFFF8CA0,
        0xFF5FD3A8,
        0xFFFFC857,
        0xFFC4A1FF,
        0xFFFF9A62,
        0xFF62D2E3,
        0xFFF28BC4,
    ];

    private static readonly string[] ColorNames = ["Blue", "Rose", "Green", "Amber", "Violet", "Orange", "Teal", "Magenta"];

    public static int NormalizeColor(int index) => ((index % PaletteSize) + PaletteSize) % PaletteSize;

    /// <summary>ARGB of palette entry <paramref name="index"/> (wraps) for the light or dark theme.</summary>
    public static uint ColorArgb(int index, bool dark) =>
        (dark ? DarkColors : LightColors)[NormalizeColor(index)];

    public static string ColorName(int index) => ColorNames[NormalizeColor(index)];

    /// <summary>First palette entry no person uses; falls back to the least used one.</summary>
    public static int NextFreeColor(IEnumerable<SpeakerPerson> people)
    {
        var counts = new int[PaletteSize];
        foreach (var person in people)
            counts[NormalizeColor(person.Color)]++;
        var best = 0;
        for (var i = 1; i < PaletteSize; i++)
        {
            if (counts[i] < counts[best])
                best = i;
        }
        return best;
    }

    // ── Wording ───────────────────────────────────────────────────────────

    public static string SpeakerCount(int count) => count == 1 ? "1 speaker" : count.ToString(CultureInfo.InvariantCulture) + " speakers";

    public static string PercentText(double percent) =>
        Math.Round(percent, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + " %";

    /// <summary>People ordered by their share of the talk time (people without stats last, keeping their order).</summary>
    public static IReadOnlyList<SpeakerPerson> ByTalkTime(SpeakerLayerView layer) =>
        layer.People
            .Select((p, i) => (Person: p, Index: i, Percent: layer.Stats.FirstOrDefault(s => s.PersonId == p.Id)?.Percent ?? -1))
            .OrderByDescending(x => x.Percent)
            .ThenBy(x => x.Index)
            .Select(x => x.Person)
            .ToList();

    public static SpeakerStat? StatOf(SpeakerLayerView layer, string personId) =>
        layer.Stats.FirstOrDefault(s => s.PersonId == personId);

    public static SpeakerPerson? PersonOf(SpeakerLayerView? layer, string? personId) =>
        personId is null ? null : layer?.People.FirstOrDefault(p => p.Id == personId);

    /// <summary>Name of a person, or "Unknown speaker" for an unlabelled segment.</summary>
    public static string NameOf(SpeakerLayerView? layer, string? personId) =>
        PersonOf(layer, personId)?.Name is { Length: > 0 } name ? name : "Unknown speaker";

    /// <summary>One line for narrow screens: <c>3 speakers · Anna 48 %, Mark 32 %…</c>.</summary>
    public static string SummaryLine(SpeakerLayerView layer)
    {
        var ordered = ByTalkTime(layer);
        var parts = new List<string>();
        foreach (var person in ordered.Take(2))
        {
            var stat = StatOf(layer, person.Id);
            parts.Add(stat is null ? person.Name : person.Name + " " + PercentText(stat.Percent));
        }
        var tail = ordered.Count > 2 ? "…" : "";
        return SpeakerCount(layer.People.Count) + (parts.Count > 0 ? " · " + string.Join(", ", parts) + tail : "");
    }

    /// <summary>Chip text of the wide layout: <c>Anna 48 %</c>.</summary>
    public static string ChipText(SpeakerLayerView layer, SpeakerPerson person) =>
        StatOf(layer, person.Id) is { } stat ? person.Name + " " + PercentText(stat.Percent) : person.Name;

    /// <summary><c>46 % · 5:47 · 7 turns</c> under a name in the Speakers sheet.</summary>
    public static string StatLine(SpeakerStat? stat)
    {
        if (stat is null)
            return "No speech detected";
        var turns = stat.Turns == 1 ? "1 turn" : stat.Turns.ToString(CultureInfo.InvariantCulture) + " turns";
        return PercentText(stat.Percent) + " · " + Duration(stat.TalkSeconds) + " · " + turns;
    }

    /// <summary><c>5:47</c>, <c>1:05:09</c>, or <c>38 s</c> below a minute.</summary>
    public static string Duration(double seconds)
    {
        var total = (int)Math.Round(Math.Max(0, seconds), MidpointRounding.AwayFromZero);
        if (total < 60)
            return total.ToString(CultureInfo.InvariantCulture) + " s";
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    /// <summary>Hint for a voice that probably is one of the others (design §10.6), or null.</summary>
    public static string? MergeHint(SpeakerLayerView layer, SpeakerPerson person)
    {
        if (layer.People.Count < 2 || StatOf(layer, person.Id) is not { } stat || stat.Percent >= MinorSpeakerPercent)
            return null;
        return $"{person.Name} talks only {Duration(stat.TalkSeconds)}. Is it the same voice as another speaker?";
    }

    /// <summary>"Up to 8 voices are told apart" warning when every channel is taken.</summary>
    public static string? VoiceLimitWarning(SpeakerLayerView layer) =>
        layer.People.Count >= MaxVoices ? "Up to 8 voices are told apart — some may be mixed." : null;

    /// <summary>Versions tab: <c>Speakers 3 · Nemotron Diarization INT8</c>.</summary>
    public static string VersionLine(SpeakerLayerView layer) =>
        "Speakers " + layer.People.Count.ToString(CultureInfo.InvariantCulture)
        + (layer.ModelTitle.Length > 0 ? " · " + layer.ModelTitle : "");

    /// <summary>Versions tab: <c>Detection offline, 30 s buffer</c>; empty when the layer has no preset.</summary>
    public static string PresetLine(SpeakerLayerView layer) =>
        layer.Preset.Length == 0 ? "" : "Detection " + layer.Preset;

    public static string FilterLine(string name, int shown, int total) =>
        $"{name} only · {shown.ToString(CultureInfo.InvariantCulture)} of {total.ToString(CultureInfo.InvariantCulture)} segments";

    public static string PreviousTurnLabel(string name) => $"Previous turn by {name}";

    public static string NextTurnLabel(string name) => $"Next turn by {name}";

    public static SpeakerBarMode BarMode(double width, double height)
    {
        if (width < 600)
            return SpeakerBarMode.Summary;
        return width < 840 || height < 480 ? SpeakerBarMode.Picker : SpeakerBarMode.Chips;
    }

    // ── Transcript rows ───────────────────────────────────────────────────

    /// <summary>Majority speaker per segment position (null = no label).</summary>
    public static string?[] PersonPerSegment(SpeakerLayerView? layer, int segmentCount)
    {
        var result = new string?[segmentCount];
        if (layer is null)
            return result;
        foreach (var s in layer.Segments)
        {
            if (s.SegmentIndex >= 0 && s.SegmentIndex < segmentCount)
                result[s.SegmentIndex] = s.PersonId;
        }
        return result;
    }

    /// <summary>
    /// Header / hidden / collapsed flags for every segment. Without a layer nothing has a header.
    /// A header opens each run by one speaker (also right after segments a filter hides).
    /// </summary>
    public static IReadOnlyList<SpeakerRowInfo> BuildRows(SpeakerLayerView? layer, int segmentCount, string? filterPersonId)
    {
        var persons = PersonPerSegment(layer, segmentCount);
        var rows = new List<SpeakerRowInfo>(segmentCount);
        var filtered = layer is not null && filterPersonId is not null;
        var runStart = -1;
        for (var i = 0; i < segmentCount; i++)
        {
            var hidden = filtered && persons[i] != filterPersonId;
            if (hidden)
            {
                if (runStart < 0)
                    runStart = i;
                rows.Add(new SpeakerRowInfo(i, persons[i], false, true, null));
                continue;
            }

            if (runStart >= 0)
            {
                CloseHiddenRun(rows, persons, layer, runStart, i);
                runStart = -1;
            }
            var header = layer is not null
                && (i == 0 || persons[i] != persons[i - 1] || (filtered && rows[i - 1].IsHidden));
            rows.Add(new SpeakerRowInfo(i, persons[i], header, false, null));
        }
        if (runStart >= 0)
            CloseHiddenRun(rows, persons, layer, runStart, segmentCount);
        return rows;
    }

    private static void CloseHiddenRun(List<SpeakerRowInfo> rows, string?[] persons, SpeakerLayerView? layer, int from, int to)
    {
        var count = to - from;
        var names = persons.Skip(from).Take(count).Distinct().Select(p => NameOf(layer, p)).ToList();
        var who = names.Count <= 2 ? string.Join(", ", names) : names[0] + ", " + names[1] + " +" + (names.Count - 2).ToString(CultureInfo.InvariantCulture);
        var text = "··· " + (count == 1 ? "1 segment" : count.ToString(CultureInfo.InvariantCulture) + " segments") + " by " + who;
        rows[from] = rows[from] with { CollapsedText = text };
    }

    public static int CountShown(IReadOnlyList<SpeakerRowInfo> rows) => rows.Count(r => !r.IsHidden);

    // ── Turn navigation (filter active: ±10 s become previous / next turn) ──

    /// <summary>Start times of the turns of <paramref name="personId"/>: maximal runs of consecutive segments by that person.</summary>
    public static IReadOnlyList<double> TurnStarts(SpeakerLayerView layer, IReadOnlyList<double> segmentStarts, string personId)
    {
        var persons = PersonPerSegment(layer, segmentStarts.Count);
        var starts = new List<double>();
        for (var i = 0; i < persons.Length; i++)
        {
            if (persons[i] == personId && (i == 0 || persons[i - 1] != personId))
                starts.Add(segmentStarts[i]);
        }
        return starts;
    }

    /// <summary>Start of the next turn after <paramref name="position"/>, or null when there is none.</summary>
    public static double? NextTurn(IReadOnlyList<double> turnStarts, double position)
    {
        foreach (var start in turnStarts)
        {
            if (start > position + 0.25)
                return start;
        }
        return null;
    }

    /// <summary>
    /// Start of the previous turn: when the playhead is more than <paramref name="restartAfter"/> seconds into a turn
    /// the first press returns to the start of that turn, like a music player. Null when there is no earlier turn.
    /// </summary>
    public static double? PreviousTurn(IReadOnlyList<double> turnStarts, double position, double restartAfter = 1.5)
    {
        double? found = null;
        foreach (var start in turnStarts)
        {
            if (start < position - restartAfter)
                found = start;
        }
        return found;
    }

    /// <summary>Index of the segment playing at <paramref name="seconds"/> (last one that started), or -1.</summary>
    public static int SegmentIndexAt(IReadOnlyList<double> segmentStarts, double seconds)
    {
        var found = -1;
        for (var i = 0; i < segmentStarts.Count && segmentStarts[i] <= seconds + 0.001; i++)
            found = i;
        return found;
    }

    // ── Words ─────────────────────────────────────────────────────────────

    /// <summary>Whether the segment carries different speakers on its words (a change inside the segment).</summary>
    public static bool HasWordChange(SegmentSpeakers segment)
    {
        string? first = null;
        var seen = false;
        foreach (var id in segment.WordPersonIds)
        {
            if (id is null)
                continue;
            if (!seen)
            {
                first = id;
                seen = true;
            }
            else if (id != first)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Text runs of a segment: an inline <c>● Name</c> marker where the speaker changes inside the segment, and
    /// (with <paramref name="colorWords"/>) every word tinted by its speaker. Empty when the segment reads as plain text.
    /// </summary>
    public static IReadOnlyList<SpeakerTextRun> BuildRuns(string text, SegmentSpeakers? labels, SpeakerLayerView? layer, bool colorWords)
    {
        if (labels is null || layer is null || labels.WordPersonIds.Count == 0)
            return [];
        var current = labels.PersonId;
        var changes = labels.WordPersonIds.Any(id => id is not null && id != current);
        if (!colorWords && !changes)
            return [];

        var runs = new List<SpeakerTextRun>();
        var buffer = new System.Text.StringBuilder();
        void Flush()
        {
            if (buffer.Length == 0)
                return;
            runs.Add(new SpeakerTextRun(buffer.ToString(), colorWords ? ColorOf(layer, current) : null, false));
            buffer.Clear();
        }

        var words = TextTokens.SplitWords(text);
        for (var i = 0; i < words.Count; i++)
        {
            var id = i < labels.WordPersonIds.Count ? labels.WordPersonIds[i] : null;
            if (id is not null && id != current)
            {
                Flush();
                current = id;
                runs.Add(new SpeakerTextRun("● " + NameOf(layer, id) + " ", ColorOf(layer, id), true));
            }
            buffer.Append(words[i]).Append(' ');
        }
        Flush();
        if (runs.Count > 0 && !runs[^1].IsMarker)
            runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
        return runs;
    }

    private static int? ColorOf(SpeakerLayerView layer, string? personId) =>
        PersonOf(layer, personId)?.Color;

    /// <summary>Whether the layer labels words at all (the word-colour switch is offered only then).</summary>
    public static bool HasWordLabels(SpeakerLayerView? layer) =>
        layer is not null && layer.Segments.Any(s => s.WordPersonIds.Any(id => id is not null));
}
