using SpeechLib.TextOutput;

namespace VoiceType.Uno.Services;

/// <summary>
/// Decides what to type into another app while a recording runs. It is fed the text the recognizer has
/// <i>committed</i> (stable, never revised) after every step and answers with the part that is new, so a phrase
/// appears in the focused window as soon as the recognizer settles on it instead of when recording stops.
/// </summary>
/// <remarks>
/// Text typed into a foreign window cannot be taken back, so while typing is not possible (injection off, manual
/// input, focus inside VoiceType itself) the text is marked as handled instead: it is never typed later, and typing
/// resumes with the next new words once the focus is on another app again.
/// </remarks>
public sealed class LiveTextFeed
{
    private readonly IncrementalTextTyper _typer = new();

    /// <summary>A new recording session starts; the end of the previous one decides the joining space.</summary>
    public void BeginSession() => _typer.Reset();

    /// <summary>
    /// The text to type so the target matches <paramref name="stableText"/> (the committed transcript of the session
    /// so far), or "" when there is nothing new or <paramref name="canType"/> is false.
    /// </summary>
    public string Next(string stableText, bool canType)
    {
        if (!canType)
        {
            _typer.SkipTo(stableText);
            return "";
        }

        return _typer.Next(stableText);
    }
}
