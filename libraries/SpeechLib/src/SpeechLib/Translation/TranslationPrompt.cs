namespace SpeechLib.Translation;

/// <summary>
/// The system prompt shared by every translation backend, so the in-process and
/// HTTP paths translate identically.
/// </summary>
public static class TranslationPrompt
{
    /// <summary>
    /// A compact instruction tuned for live speech: the input is a transcript that may
    /// lack punctuation, cut off mid-sentence and carry recognition slips, and the reply
    /// must be nothing but the translation (no labels, quotes, notes or JSON).
    /// </summary>
    public static string BuildSystemPrompt(string targetLang, string? sourceLang, string? additionalInstructions = null)
    {
        var source = string.IsNullOrWhiteSpace(sourceLang) ? "the source language" : sourceLang.Trim();
        var target = string.IsNullOrWhiteSpace(targetLang) ? "the target language" : targetLang.Trim();

        var prompt =
            "You are a professional translation engine for live speech. " +
            $"Translate the user's message from {source} into {target}. " +
            "The message is a live transcript and may stop mid-sentence: translate exactly what is there " +
            "without completing, correcting, or commenting on it. " +
            "Keep names, numbers, units, punctuation, and capitalization as in the message. " +
            $"If the message is already in {target}, return it unchanged. " +
            "Reply with the translation only: no preamble, labels, quotes, notes, or JSON.";

        return string.IsNullOrWhiteSpace(additionalInstructions)
            ? prompt
            : prompt + "\n\nAdditional instructions:\n" + additionalInstructions.Trim();
    }
}
