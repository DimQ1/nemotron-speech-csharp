namespace SpeechLib;

/// <summary>
/// Optional recognizer capability for changing settings while the model stays loaded.
/// </summary>
public interface IRuntimeConfigurable
{
    bool TrySetVad(bool enabled);
    bool TrySetSearchOptions(int numBeams, double repetitionPenalty);
}