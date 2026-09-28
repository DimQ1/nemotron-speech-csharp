namespace SpeechLib.Translation;

/// <summary>
/// One translation job: the text plus everything a backend may use to translate it
/// better. Live transcripts arrive one sentence at a time, so the previous sentence
/// and its translation are offered as context (pronouns, terminology, register); a
/// backend that cannot use context simply ignores it.
/// </summary>
/// <param name="Text">Source text (a sentence or an unfinished tail).</param>
/// <param name="TargetLanguage">Target language as a human-readable name ("Russian"), the form
/// instruction-tuned models follow most reliably; see <see cref="TranslationLanguages"/>.</param>
public sealed record TranslationRequest(string Text, string TargetLanguage)
{
    /// <summary>Source language name when known (the ASR language setting); null = let the model detect it.</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>The sentence translated just before this one, when any.</summary>
    public string? PreviousSource { get; init; }

    /// <summary>The translation produced for <see cref="PreviousSource"/>.</summary>
    public string? PreviousTranslation { get; init; }

    /// <summary>True when a previous sentence/translation pair is available as context.</summary>
    public bool HasContext =>
        !string.IsNullOrWhiteSpace(PreviousSource) && !string.IsNullOrWhiteSpace(PreviousTranslation);
}
