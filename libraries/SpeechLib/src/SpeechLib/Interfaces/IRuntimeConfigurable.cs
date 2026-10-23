namespace SpeechLib;

/// <summary>
/// Optional recognizer capability for changing settings while the model stays loaded.
/// </summary>
public interface IRuntimeConfigurable
{
    /// <summary>Switches the silence gate on or off while the model stays loaded; false when it cannot (no VAD model).</summary>
    bool TrySetVad(bool enabled);

    /// <summary>
    /// Beam count and repetition penalty. <b>No recognizer acts on either value any more</b>: the Nemotron and Parakeet
    /// transducers search greedily and the GenAI recognizer that read them is gone, so an implementation accepts and
    /// ignores the values (Nemotron returns true) or says false (VibeVoice). The settings screens still offer the penalty
    /// and History still records it, for old settings files; changing it has no effect on the text.
    /// </summary>
    bool TrySetSearchOptions(int numBeams, double repetitionPenalty);
}