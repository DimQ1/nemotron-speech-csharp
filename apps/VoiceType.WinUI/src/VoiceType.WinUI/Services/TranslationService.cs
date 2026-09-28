using SpeechLib;
using SpeechLib.LiteRT.Native;
using SpeechLib.Translation;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Live translation for the WinUI head: a thin adapter over the shared
/// <see cref="LiveTranslationSession"/> (sentence buffering, drafts, stable-prefix
/// locking, context, memo) that supplies the in-process Gemma 4 backend and knows
/// where the model file lives.
/// </summary>
/// <remarks>
/// <see cref="Feed"/>, <see cref="FlushAsync"/> and <see cref="Reset"/> are expected on
/// the UI thread. Translation runs on the thread pool; the events are raised from
/// worker threads.
/// </remarks>
public sealed class TranslationService : ITranslationService
{
    private readonly LiveTranslationSession _session;
    private volatile string _computeBackend = "cpu";

    public TranslationService()
    {
        _session = new LiveTranslationSession(CreateTranslatorAsync);
        _session.TranslationChanged += text => TranslationChanged?.Invoke(text);
        _session.StatusChanged += status => StatusChanged?.Invoke(status);
    }

    public event Action<string>? TranslationChanged;
    public event Action<string>? StatusChanged;

    public bool IsModelAvailable => TranslationModelInfo.IsDownloaded;
    public bool IsLoaded => _session.IsLoaded;
    public bool IsLoading => _session.IsLoading;
    public string StatusText => _session.StatusText;

    public void SetTargetLanguage(string language) =>
        _session.TargetLanguage = TranslationLanguages.NameFor(language) ?? language;

    public void SetSourceLanguage(string? languageCode) =>
        _session.SourceLanguage = TranslationLanguages.NameFor(languageCode);

    /// <summary>
    /// Selects the compute backend for the native engine: "cpu" (XNNPACK) or
    /// "gpu" (WebGPU delegate). The loaded engine is released once the decode in
    /// flight finishes; the next translation reloads with the new backend.
    /// </summary>
    public void SetComputeBackend(string backend)
    {
        var normalized = (backend ?? "cpu").Trim().ToLowerInvariant();
        if (normalized is not ("cpu" or "gpu"))
            normalized = "cpu";

        if (_computeBackend == normalized)
            return;

        _computeBackend = normalized;
        _ = ReplaceTranslatorSafelyAsync();
    }

    private async Task ReplaceTranslatorSafelyAsync()
    {
        try
        {
            await _session.ReplaceTranslatorAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"Translation engine error: {ex.Message}");
        }
    }

    public void Feed(string fullText) => _session.Feed(fullText);

    public Task FlushAsync(CancellationToken cancellationToken = default) => _session.FlushAsync(cancellationToken);

    public void Reset() => _session.Reset();

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_session.IsLoaded)
            return;

        if (!IsModelAvailable)
        {
            StatusChanged?.Invoke("Translation model not downloaded");
            return;
        }

        await _session.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _session.DisposeAsync();

    private Task<ITextTranslator> CreateTranslatorAsync(CancellationToken cancellationToken)
    {
        var backend = _computeBackend;
        return Task.Run<ITextTranslator>(() => new LiteRTLmNativeTranslator(new LiteRTLmNativeOptions
        {
            ModelPath = TranslationModelInfo.LocalModelPath,
            Backend = backend,
            LogLevel = LiteRTLmLogLevel.Silent,
        }), cancellationToken);
    }
}
