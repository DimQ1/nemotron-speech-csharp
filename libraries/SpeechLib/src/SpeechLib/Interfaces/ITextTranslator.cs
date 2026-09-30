using System.Runtime.CompilerServices;
using SpeechLib.Translation;

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

    /// <summary>
    /// Translates a <see cref="TranslationRequest"/>. Backends that can exploit the
    /// request's context (previous sentence pair) override this; the default ignores it.
    /// </summary>
    Task<string?> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default) =>
        TranslateAsync(request.Text, request.TargetLanguage, request.SourceLanguage, cancellationToken);

    /// <summary>Streaming form of <see cref="TranslateAsync(TranslationRequest, CancellationToken)"/>.</summary>
    IAsyncEnumerable<string> TranslateStreamAsync(TranslationRequest request, CancellationToken cancellationToken = default) =>
        TranslateStreamAsync(request.Text, request.TargetLanguage, request.SourceLanguage, cancellationToken);
}
