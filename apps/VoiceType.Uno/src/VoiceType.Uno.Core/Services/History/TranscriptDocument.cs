using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceType.Uno.History;

/// <summary><c>transcripts/vN.json</c>: segments with word timings.</summary>
public sealed class TranscriptDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>Version id this file belongs to (e.g. <c>v2</c>).</summary>
    public string Version { get; set; } = "";

    /// <summary>Full text (segments joined with a space).</summary>
    public string Text { get; set; } = "";

    public List<TranscriptSegment> Segments { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>Joins the segment texts the way <see cref="Text"/> is stored.</summary>
    public static string JoinSegments(IEnumerable<TranscriptSegment> segments) =>
        string.Join(" ", segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));

    /// <summary>Total words over all segments (whitespace-separated).</summary>
    [JsonIgnore]
    public int WordCount => Segments.Sum(s => TextTokens.SplitWords(s.Text).Count);
}

public sealed class TranscriptSegment
{
    [JsonPropertyName("i")]
    public int Index { get; set; }

    /// <summary>Seconds from the start of the audio.</summary>
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<TranscriptWord> Words { get; set; } = [];

    /// <summary>One of <see cref="WordTimingKinds"/>.</summary>
    public string WordTiming { get; set; } = WordTimingKinds.Estimated;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class TranscriptWord
{
    public TranscriptWord() { }

    public TranscriptWord(string text, double start, double end)
    {
        Text = text;
        Start = start;
        End = end;
    }

    [JsonPropertyName("w")]
    public string Text { get; set; } = "";

    [JsonPropertyName("s")]
    public double Start { get; set; }

    [JsonPropertyName("e")]
    public double End { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary><c>translations/tN.&lt;lang&gt;.&lt;ver&gt;.json</c>: one entry per source segment.</summary>
public sealed class TranslationDocument
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public string SourceVersion { get; set; } = "";
    public string TargetLanguage { get; set; } = "";

    /// <summary>Index matches <see cref="TranscriptSegment.Index"/> of the source version.</summary>
    public List<TranslationSegment> Segments { get; set; } = [];
    public int SentencesDone { get; set; }
    public int SentencesTotal { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public string? TextFor(int segmentIndex) => Segments.Find(s => s.Index == segmentIndex)?.Text;
}

public sealed class TranslationSegment
{
    [JsonPropertyName("i")]
    public int Index { get; set; }
    public string Text { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
