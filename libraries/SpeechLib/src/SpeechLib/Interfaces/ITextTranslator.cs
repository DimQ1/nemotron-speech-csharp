using System.Runtime.CompilerServices;

namespace SpeechLib;

/// <summary>
/// Text-to-text translation abstraction. ASR providers produce a transcript;
/// implementations translate it into another language (e.g., via a local LiteRT-LM
/// server in the gemma-translator topology).
/// </summary>
public interface ITextTranslator : IDisposable
{
    /// <summary>
    /// Translates <paramref name="text"/> into <paramref name="targetLang"/>.
    /// Returns the translated text, or null when no translation was produced.
    /// </summary>
    Task<string?> TranslateAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous convenience wrapper for pipeline code that already runs on a
    /// dedicated thread. Defaults to blocking on <see cref="TranslateAsync"/>.
    /// </summary>
    string? Translate(string text, string targetLang, string? sourceLang = null) =>
        TranslateAsync(text, targetLang, sourceLang).GetAwaiter().GetResult();

    /// <summary>
    /// Translates <paramref name="text"/> incrementally, yielding translated text
    /// as it is produced (token deltas). Implementations that cannot stream fall
    /// back to emitting the full <see cref="TranslateAsync"/> result as one delta.
    /// </summary>
    async IAsyncEnumerable<string> TranslateStreamAsync(
        string text,
        string targetLang,
        string? sourceLang = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await TranslateAsync(text, targetLang, sourceLang, cancellationToken);
        if (result is not null)
            yield return result;
    }
}
