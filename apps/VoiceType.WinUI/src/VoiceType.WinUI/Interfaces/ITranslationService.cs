namespace VoiceType.WinUI.Interfaces;

/// <summary>
/// Coordinates on-the-fly machine translation of streaming ASR text using the
/// in-process LiteRT-LM translator (Gemma 4). Feeds a growing transcript, splits
/// off complete sentences, and streams each sentence's translation back to the UI.
/// </summary>
public interface ITranslationService : IAsyncDisposable
{
    /// <summary>Raised (on a worker thread) with the full cumulative translated text
    /// whenever a sentence completes or a new token arrives.</summary>
    event Action<string>? TranslationChanged;

    /// <summary>Raised (on a worker thread) with a human-readable status message.</summary>
    event Action<string>? StatusChanged;

    /// <summary>True when the LiteRT model file exists on disk.</summary>
    bool IsModelAvailable { get; }

    /// <summary>True when the translator engine is loaded and ready.</summary>
    bool IsLoaded { get; }

    /// <summary>True while the translator engine is loading.</summary>
    bool IsLoading { get; }

    /// <summary>Latest human-readable status (e.g. "Model ready", "Loading model...").</summary>
    string StatusText { get; }

    /// <summary>Sets the target language for subsequently-enqueued sentences.</summary>
    void SetTargetLanguage(string language);

    /// <summary>Feeds the current full recognized text; only the new suffix is processed.</summary>
    void Feed(string fullText);

    /// <summary>Translates the remaining tail and waits for all in-flight translations.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears the sentence buffer and the accumulated translated output.</summary>
    void Reset();

    /// <summary>Loads the translator engine (no-op when already loaded or when the model
    /// file is missing). Returns when the engine is ready or an error has been recorded.</summary>
    Task EnsureLoadedAsync(CancellationToken cancellationToken = default);
}
