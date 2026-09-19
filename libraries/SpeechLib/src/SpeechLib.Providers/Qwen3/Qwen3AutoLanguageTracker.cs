namespace SpeechLib.Qwen3;

internal sealed class Qwen3AutoLanguageTracker
{
    private string? _candidate;
    private int _candidateVotes;

    public bool Observe(string languagePrefix, out string? lockedLanguage)
    {
        lockedLanguage = null;
        if (!Qwen3Prompt.TryDetectLanguage(languagePrefix, out var detected)
            || detected is null)
            return false;

        if (string.Equals(_candidate, detected, StringComparison.OrdinalIgnoreCase))
        {
            _candidateVotes++;
        }
        else
        {
            _candidate = detected;
            _candidateVotes = 1;
        }

        if (_candidateVotes < 2)
            return true;

        lockedLanguage = detected;
        Reset();
        return true;
    }

    public void Reset()
    {
        _candidate = null;
        _candidateVotes = 0;
    }
}