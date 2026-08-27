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
/// </param>
public readonly record struct StreamingResult(string Partial, string? Final)
{
    /// <summary>True when this step committed at least one utterance.</summary>
    public bool HasFinal => Final is not null;
}
