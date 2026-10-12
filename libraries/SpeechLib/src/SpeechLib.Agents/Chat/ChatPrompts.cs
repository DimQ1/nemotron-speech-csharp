using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SpeechLib.Agents;

/// <summary>How the question wants to read the recordings; decides what the context contains.</summary>
public enum QuestionKind
{
    /// <summary>Asks about something specific: retrieve the best matching passages.</summary>
    Specific,

    /// <summary>Summary / overview: needs the whole recording, so long ones go through the cached digest.</summary>
    Overview,

    /// <summary>Action items, decisions, questions, who said what: spread over the recording, found by cue words.</summary>
    Extraction,
}

public sealed record QuickPromptText(string Id, string Label, string Prompt, QuestionKind Kind);

/// <summary>The words given to the model. Kept short: the model is a 2B-class on-device Gemma.</summary>
public static partial class ChatPrompts
{
    public const string Instructions =
        "You answer questions about audio recordings using their transcripts.\n" +
        "Rules:\n" +
        "1. Use only the transcript text given with the question. If it does not contain the answer, say so in one sentence and do not add a citation.\n" +
        "2. Put a citation after every fact, written exactly as [S1 03:20]: the source tag and the time shown at the start of the transcript line the fact came from. Never invent a source or a time.\n" +
        "3. Answer in the language of the question. Be concise.\n" +
        "4. When several recordings are involved, say which recording each fact comes from.\n" +
        "5. The transcript is automatic speech recognition without speaker labels and may contain mistakes. Do not guess who spoke or invent names.";

    /// <summary>Rule 5 when the transcript lines carry speaker names ("Anna: text") from speaker detection.</summary>
    public const string SpeakerRule =
        "5. Many transcript lines start with a speaker name, as in \"Anna: text\"; use the names exactly as given when you say who spoke. " +
        "A line without a name has an unknown speaker; never invent a name for it or for \"Speaker N\". The transcript is automatic speech recognition and may contain mistakes.";

    // Bump when the wording of a summarisation prompt changes: cached summaries are keyed by it.
    private const string TranscriptLanguage = " Answer in the language of the transcript.";

    public static IReadOnlyList<QuickPromptText> QuickPrompts { get; } =
    [
        new("summary", "Summary",
            "Summarize this in at most 7 short bullet points, in the order things happen. Cite the time of each point." + TranscriptLanguage,
            QuestionKind.Overview),
        new("actions", "Action items",
            "List the action items: who has to do what, and by when if it is said. One short bullet each, at most 8 bullets, each with a citation. If there are none, say so." + TranscriptLanguage,
            QuestionKind.Extraction),
        new("decisions", "Key decisions",
            "List the key decisions that were made or agreed: one short bullet each, at most 8, each with a citation. If there are none, say so." + TranscriptLanguage,
            QuestionKind.Extraction),
        new("questions", "Questions asked",
            "List the questions that were asked and whether they were answered: one short bullet each, at most 8, each with a citation." + TranscriptLanguage,
            QuestionKind.Extraction),
        new("speakers", "Who said what",
            "Who spoke and what did each person say? When transcript lines start with a speaker name, group the points by those names. Otherwise use only names that are said in the transcript, and if people cannot be told apart, say that and list the main points instead. At most 8 short bullets, each with a citation." + TranscriptLanguage,
            QuestionKind.Extraction),
    ];

    /// <summary>Heading of the sources block shown in the system message.</summary>
    public static string BuildSystemMessage(IReadOnlyList<ChatSourceDocument> sources)
    {
        var active = sources.Where(s => s.Active).ToList();
        var sb = new StringBuilder(active.Any(s => HasSpeakers(s)) ? Instructions.Replace(NoSpeakerRule, SpeakerRule, StringComparison.Ordinal) : Instructions);
        sb.Append("\n\nRecordings you can read:");
        foreach (var s in active)
        {
            sb.Append("\n[").Append(s.Alias).Append("] \"").Append(Shorten(s.Title, 80)).Append('"');
            if (s.Date is { } d)
                sb.Append(", ").Append(d.ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture));
            if (s.DurationSec > 0)
                sb.Append(", ").Append(TimeText.Format(s.DurationSec)).Append(" long");
        }
        var removed = sources.Where(s => !s.Active).Select(s => s.Alias).ToList();
        if (removed.Count > 0)
            sb.Append("\n").Append(string.Join(", ", removed)).Append(removed.Count == 1 ? " was" : " were").Append(" removed by the user: do not cite or use it.");
        return sb.ToString();
    }

    private const string NoSpeakerRule =
        "5. The transcript is automatic speech recognition without speaker labels and may contain mistakes. Do not guess who spoke or invent names.";

    private static bool HasSpeakers(ChatSourceDocument source)
    {
        try
        {
            return source.LoadLines().Any(l => !string.IsNullOrWhiteSpace(l.Speaker));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    public static QuestionKind Classify(string question)
    {
        var text = question.ToLowerInvariant();
        foreach (var quick in QuickPrompts)
        {
            if (string.Equals(question.Trim(), quick.Prompt, StringComparison.Ordinal))
                return quick.Kind;
        }
        if (ExtractionRegex().IsMatch(text))
            return QuestionKind.Extraction;
        if (OverviewRegex().IsMatch(text))
            return QuestionKind.Overview;
        return QuestionKind.Specific;
    }

    /// <summary>Extra retrieval terms for extraction questions: cue words that mark commitments, decisions, questions.</summary>
    public static string ExtractionCues(string question)
    {
        var text = question.ToLowerInvariant();
        var cues = new List<string>();
        if (Regex.IsMatch(text, "action|todo|task|assign|должен|задач|действи|поруч|сделат"))
            cues.Add("will need must should deadline by tomorrow friday monday next week assign responsible todo follow up send prepare " +
                     "нужно надо должен должна сделать до завтра пятницы понедельника неделе подготовить отправить ответственный срок");
        if (Regex.IsMatch(text, "decision|decide|agree|решени|решил|договор|согласов"))
            cues.Add("decided decision agreed agree approved let's go with we will choose решили решение договорились согласовали утвердили выбрали");
        if (Regex.IsMatch(text, "question|asked|вопрос|спросил"))
            cues.Add("question ask asked wonder whether how why what вопрос спросил как почему зачем когда ли");
        if (Regex.IsMatch(text, "who|speak|said|name|кто|говорил|сказал|имя"))
            cues.Add("said says told asked name i'm this is hello hi speaking сказал говорит меня зовут привет это");
        return string.Join(' ', cues);
    }

    [GeneratedRegex(@"action item|to-?do|next steps|decision|decided|agreed|questions? (were )?asked|who said|who spoke|speakers?|задач|что нужно сделать|решени|договор|кто что сказал|кто говорил|вопрос(ы|ов)? (был|зад)", RegexOptions.IgnoreCase)]
    private static partial Regex ExtractionRegex();

    [GeneratedRegex(@"summar|overview|key points|main points|recap|tl;?dr|gist|what is (this|it) about|what (was|is) discussed|about what|резюм|кратк|суть|основн\w+ (мысл|пункт|тем)|о чем|о чём|перескаж|итог", RegexOptions.IgnoreCase)]
    private static partial Regex OverviewRegex();

    public static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";
}
