using System.Text;
using SpeechLib;
using SpeechLib.Models;
using SpeechLib.PostProcessing;

namespace VoiceType.Uno.Services;

/// <summary>
/// One decoded batch: the text to show as the live partial, and the text (when any)
/// this batch committed to the transcript.
/// </summary>
/// <param name="Tail">The recognizer's revisable, uncommitted text after this batch.</param>
/// <param name="CommittedWords">The decoder's word timings of <paramref name="Committed"/>, when the recognizer has them.</param>
/// <param name="CommittedContinuesPrevious">
/// <paramref name="Committed"/> starts with the rest of the previous utterance's last word
/// (<see cref="StreamingResult.FinalContinuesPrevious"/>).
/// </param>
public readonly record struct RecognitionStep(
    string? Display, string? Committed, bool IsUtteranceCommit, string? Tail = null,
    IReadOnlyList<WordTiming>? CommittedWords = null, bool CommittedContinuesPrevious = false)
{
    public static RecognitionStep Empty { get; } = new(null, null, false);
}

/// <summary>
/// Unifies the two recognizer shapes behind one per-batch step. SpeechLib exposes
/// utterance-segmented streaming (committed text plus a revisable partial) and plain
/// streaming (a running transcript); the decode loop must not care which one it drives.
/// </summary>
public interface IRecognitionPipeline
{
    /// <summary>The transcript committed so far.</summary>
    string CommittedText { get; }

    /// <summary>
    /// <see cref="CommittedText"/> plus the stable part of the utterance still in progress: text that will not
    /// change any more, so it may be typed into another app before the utterance ends. It only ever grows,
    /// which is what the typing into another app relies on.
    /// </summary>
    string StableText { get; }

    /// <summary>Drops committed text for a new session.</summary>
    void Reset();

    RecognitionStep Process(float[] batch);

    RecognitionStep Flush();
}

public static class RecognitionPipelineFactory
{
    public static IRecognitionPipeline Create(IStreamingSpeechRecognizer recognizer) =>
        recognizer is IUtteranceStreamingRecognizer utterance
            ? new UtterancePipeline(utterance)
            : new PlainPipeline(recognizer);
}

/// <summary>
/// Utterance-segmented recognizer (Parakeet TDT, Nemotron): finalized utterances are committed
/// with a separating space (<see cref="UtteranceText"/>: none in front of a closing mark that the
/// recognizer reports with the next utterance, or the rest of a late word), the open
/// utterance is shown as the partial.
/// </summary>
public sealed class UtterancePipeline(IUtteranceStreamingRecognizer recognizer) : IRecognitionPipeline
{
    // A Final can carry the closing language tag (History reads the language from it), the open utterance never does: the
    // stable text is composed from the tag-free Finals, otherwise the doubled blank left by a tag in the middle of a Final
    // would make it differ from the text typed so far and the typing would drop what follows the tag.
    private static readonly PostProcessingChain TagFree =
        new PostProcessingChain().Add(new LanguageTagStripper()).Add(new WhitespaceNormalizer());

    private readonly StringBuilder _committed = new();
    private readonly StringBuilder _committedTagFree = new();
    private string _stable = "";
    private bool _stableContinuesPrevious;

    public string CommittedText => _committed.ToString();

    public string StableText => UtteranceText.Join(_committedTagFree.ToString(), _stable, _stableContinuesPrevious);

    public void Reset()
    {
        _committed.Clear();
        _committedTagFree.Clear();
        _stable = "";
        _stableContinuesPrevious = false;
    }

    public RecognitionStep Process(float[] batch) => ToStep(recognizer.ProcessUtterance(batch));

    public RecognitionStep Flush() => ToStep(recognizer.FlushUtterance());

    private RecognitionStep ToStep(StreamingResult result)
    {
        var committed = string.IsNullOrEmpty(result.Final) ? null : result.Final;
        if (committed is not null)
        {
            UtteranceText.Append(_committed, committed, result.FinalContinuesPrevious);
            UtteranceText.Append(_committedTagFree, TagFree.Execute(committed), result.FinalContinuesPrevious);
        }

        // The stable part of the utterance that is still going on (Stable is null for recognizers that do not report it).
        _stable = result.Stable ?? "";
        _stableContinuesPrevious = result.PartialContinuesPrevious;

        return new RecognitionStep(
            UtteranceText.Join(_committed.ToString(), result.Partial, result.PartialContinuesPrevious),
            committed,
            IsUtteranceCommit: committed is not null,
            result.Partial,
            committed is null ? null : result.FinalWords,
            committed is not null && result.FinalContinuesPrevious);
    }
}

/// <summary>
/// Plain streaming recognizer: every step's text is committed verbatim (no separator)
/// and the recognizer's own <c>PartialText</c> is the revisable tail.
/// </summary>
public sealed class PlainPipeline(IStreamingSpeechRecognizer recognizer) : IRecognitionPipeline
{
    private readonly StringBuilder _committed = new();

    public string CommittedText => _committed.ToString();

    // Every step's text is committed verbatim, so it is all stable.
    public string StableText => CommittedText;

    public void Reset() => _committed.Clear();

    public RecognitionStep Process(float[] batch)
    {
        var raw = recognizer.ProcessAudio(batch);
        if (!string.IsNullOrEmpty(raw))
            _committed.Append(raw);

        var partial = recognizer.PartialText;
        return new RecognitionStep(ComposeDisplay(_committed, partial), raw, IsUtteranceCommit: false, partial);
    }

    public RecognitionStep Flush()
    {
        var tail = recognizer.Flush();
        if (!string.IsNullOrEmpty(tail))
            _committed.Append(tail);

        // Committed but not an utterance commit: nothing is raised for it (the final
        // result carries the whole text); the History tap still sees the tail.
        return new RecognitionStep(null, string.IsNullOrEmpty(tail) ? null : tail, IsUtteranceCommit: false);
    }

    /// <summary>Committed transcript followed by the recognizer's revisable tail.</summary>
    private static string ComposeDisplay(StringBuilder committed, string? partial)
    {
        if (committed.Length == 0)
            return partial ?? "";

        return string.IsNullOrEmpty(partial) ? committed.ToString() : committed + " " + partial;
    }
}
