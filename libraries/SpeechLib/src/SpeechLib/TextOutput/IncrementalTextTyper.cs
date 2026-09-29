namespace SpeechLib.TextOutput;

/// <summary>
/// Turns a growing transcript into the keystrokes to send to another application.
/// Text typed into a foreign window cannot be taken back, so only text that will
/// not change should be fed here (committed recognizer output, not the revisable
/// preview); what was already typed is never repeated.
/// </summary>
/// <remarks>
/// The feed is expected to extend what came before. When it does not (a final
/// post-processing pass normalized spacing, a tag was stripped), the typer keeps
/// the word count it has already typed and continues after it, so the target
/// never receives the same words twice.
/// </remarks>
public sealed class IncrementalTextTyper
{
    private string _typed = "";
    private int _typedWords;
    private bool _emittedAny;

    /// <summary>Text typed so far in this session.</summary>
    public string Typed => _typed;

    /// <summary>Text that ended the previous session (used to decide the joining space).</summary>
    public string PreviousTail { get; private set; } = "";

    /// <summary>Starts a new session; <see cref="PreviousTail"/> keeps the end of the last one.</summary>
    public void Reset()
    {
        if (_typed.Length > 0)
            PreviousTail = _typed.Length > 20 ? _typed[^20..] : _typed;
        _typed = "";
        _typedWords = 0;
        _emittedAny = false;
    }

    /// <summary>Marks <paramref name="text"/> as already present without typing it (injection was off).</summary>
    public void SkipTo(string text)
    {
        _typed = text ?? "";
        _typedWords = CountWords(_typed);
        _emittedAny = _typed.Length > 0;
    }

    /// <summary>Returns what to type so the target matches <paramref name="stableText"/> (possibly "").</summary>
    public string Next(string stableText)
    {
        stableText ??= "";
        if (stableText.Length == 0)
            return "";

        string delta;
        if (stableText.StartsWith(_typed, StringComparison.Ordinal))
        {
            delta = stableText[_typed.Length..];
        }
        else
        {
            // Earlier text was rewritten: continue after the words already typed.
            var start = WordStart(stableText, _typedWords);
            if (start < 0)
            {
                _typed = stableText;
                _typedWords = CountWords(stableText);
                return "";
            }
            delta = " " + stableText[start..];
        }

        _typed = stableText;
        _typedWords = CountWords(stableText);

        if (delta.Length == 0)
            return "";

        // The first text of a session never starts with punctuation or blanks; it gets
        // a single space when it continues text from a previous session.
        if (!_emittedAny)
        {
            var trimmed = delta.TrimStart();
            var i = 0;
            while (i < trimmed.Length && char.IsPunctuation(trimmed[i]))
                i++;
            trimmed = trimmed[i..].TrimStart();
            if (trimmed.Length == 0)
                return "";
            _emittedAny = true;
            return NeedsJoiningSpace(PreviousTail) ? " " + trimmed : trimmed;
        }

        return delta;
    }

    private static bool NeedsJoiningSpace(string previousTail) =>
        previousTail.Length > 0 && !char.IsWhiteSpace(previousTail[^1]);

    private static int CountWords(string text)
    {
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }
        return count;
    }

    /// <summary>Index of the start of word number <paramref name="wordIndex"/> (0-based), or -1.</summary>
    private static int WordStart(string text, int wordIndex)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) || (i > 0 && !char.IsWhiteSpace(text[i - 1])))
                continue;
            if (count == wordIndex)
                return i;
            count++;
        }
        return -1;
    }
}
