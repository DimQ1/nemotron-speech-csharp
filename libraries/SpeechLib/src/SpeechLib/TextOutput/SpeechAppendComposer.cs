namespace SpeechLib.TextOutput;

/// <summary>
/// Keeps an editable transcript field and a speech recognizer in step. The field is
/// the source of truth: speech is appended after the text the field held when
/// dictation started or resumed, so switching between typing and dictation never
/// resets what the user wrote or edited.
/// </summary>
/// <remarks>
/// The recognizer reports its whole session text on every update, and it may revise
/// the provisional tail. Words the recognizer had already produced when dictation
/// resumed (the baseline) are skipped by count, so a revised tail does not duplicate
/// text; an update that shares no leading word with the baseline means the recognizer
/// started a new session, and all of its text is new.
/// </remarks>
public sealed class SpeechAppendComposer
{
    private string _base = "";
    private string _separator = " ";
    private string[] _baselineWords = [];

    /// <summary>
    /// Speech from now on is appended after <paramref name="fieldText"/>.
    /// <paramref name="recognizerText"/> is what the recognizer has produced so far in
    /// its current session; it is already accounted for and is not appended again.
    /// <paramref name="separator"/> goes between the field text and the speech when
    /// the field text does not already end with whitespace.
    /// </summary>
    public void ContinueAfter(string? fieldText, string? recognizerText, string separator = " ")
    {
        _base = fieldText ?? "";
        _separator = separator;
        _baselineWords = SplitWords(recognizerText);
    }

    /// <summary>Returns the field text for the recognizer's current session text.</summary>
    public string Compose(string? recognizerText)
    {
        var text = recognizerText ?? "";
        string spoken;
        if (_baselineWords.Length == 0)
        {
            spoken = text.Trim();
        }
        else
        {
            var words = SplitWords(text);
            if (words.Length == 0 || !string.Equals(words[0], _baselineWords[0], StringComparison.Ordinal))
            {
                // New recognizer session: nothing in it was part of the baseline.
                _baselineWords = [];
                spoken = text.Trim();
            }
            else
            {
                spoken = string.Join(' ', words.Skip(_baselineWords.Length));
            }
        }

        if (_base.Length == 0 || spoken.Length == 0)
            return _base + spoken;
        return char.IsWhiteSpace(_base[^1]) ? _base + spoken : _base + _separator + spoken;
    }

    private static string[] SplitWords(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
}
