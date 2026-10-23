namespace SpeechLib;

/// <summary>
/// Result of one streaming recognition step, distinguishing uncommitted text
/// from text committed at an end-of-utterance boundary.
/// </summary>
/// <param name="Partial">
/// Uncommitted text accumulated since the last detected end-of-utterance.
/// This text may change as more audio arrives.
/// </param>
/// <param name="Final">
/// Text of an utterance committed at an end-of-utterance boundary crossed
/// during this step, or <see langword="null"/> when no utterance ended.
/// See <see cref="FinalContinuesPrevious"/> for how to join the texts of consecutive utterances.
/// </param>
public readonly record struct StreamingResult(string Partial, string? Final)
{
    /// <summary>True when this step committed at least one utterance.</summary>
    public bool HasFinal => Final is not null;

    /// <summary>
    /// True when <see cref="Final"/> continues the text of the previous utterance and is joined to it without a space: it starts
    /// with the closing mark of the previous sentence (a transducer that decides the punctuation when it hears what follows can
    /// write it after the recognizer's silence rule committed the sentence: <c>". You are so rude"</c>) or with the rest of a
    /// word whose pieces were emitted more than the silence rule apart. Join with
    /// <see cref="UtteranceText.Join(string?, string?, bool)"/>. False for recognizers that never do this.
    /// </summary>
    public bool FinalContinuesPrevious { get; init; }

    /// <summary>
    /// The same for <see cref="Partial"/> (and <see cref="Stable"/>, which is a part of it): the open utterance begins with the
    /// closing mark of the previous sentence or the rest of a word, so the live text joins it to the committed text without a
    /// space (<see cref="UtteranceText.Join(string?, string?, bool)"/>). When the utterance ends, its
    /// <see cref="FinalContinuesPrevious"/> is true as well.
    /// </summary>
    public bool PartialContinuesPrevious { get; init; }

    /// <summary>
    /// The part of <see cref="Partial"/> that will not change any more (decoded with
    /// full right context) but whose utterance has not ended yet; excludes any
    /// revisable preview. Null when the recognizer does not distinguish the two.
    /// Consumers that cannot take text back (typing into another app) use this.
    /// </summary>
    public string? Stable { get; init; }

    /// <summary>
    /// Words of <see cref="Final"/> with exact decoder timings on the stream's absolute
    /// timeline, when the recognizer provides them (<see cref="ITimedUtteranceRecognizer"/>);
    /// otherwise null.
    /// </summary>
    public IReadOnlyList<Models.WordTiming>? FinalWords { get; init; }

    /// <summary>
    /// Words of <see cref="Partial"/> (committed words followed by the revisable preview)
    /// with decoder timings, or null when not provided. Revisable: later steps may change them.
    /// </summary>
    public IReadOnlyList<Models.WordTiming>? PartialWords { get; init; }
}
