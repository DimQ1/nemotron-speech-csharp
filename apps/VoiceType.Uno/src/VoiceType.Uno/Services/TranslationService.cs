using SpeechLib;
using SpeechLib.LiteRT;
using SpeechLib.LiteRT.Native;
using SpeechLib.Translation;

namespace VoiceType.Uno.Services;

/// <summary>
/// Live translation for the UNO heads: a thin adapter over the shared
/// <see cref="LiveTranslationSession"/> (the same engine VoiceType.WinUI uses) that
/// picks the backend — the in-process Gemma 4 model when it is downloaded, otherwise
/// an OpenAI-compatible LiteRT-LM server — and reconnects when settings change.
/// </summary>
public sealed class TranslationService : IDisposable
{
    public enum BackendKind { Native, Http }

    private readonly LiveTranslationSession _session;
    private readonly LiteRTLmOptions _baseOptions;
    private volatile string _serverUrl;
    private volatile BackendKind _backend;
    private volatile string _computeBackend = "cpu";
    private volatile string _additionalSystemPrompt = "";
    private volatile BackendKind _activeEngine;

    public TranslationService(LiteRTLmOptions options, BackendKind backend = BackendKind.Native)
    {
        _baseOptions = options ?? throw new ArgumentNullException(nameof(options));
        _serverUrl = options.BaseUrl;
        _backend = backend;
        _session = new LiveTranslationSession(
            CreateTranslatorAsync,
            new LiveTranslationOptions { DraftDebounceMs = 80, MaxTailChars = 200 });
        _session.TranslationChanged += text => TranslationChanged?.Invoke(text);
        _session.StatusChanged += status => StatusChanged?.Invoke(status);
        Log($"created with backend={backend}, maxOutputTokens={options.MaxTokens}, model={options.Model}");
    }

    private static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"[Translate] {message}");
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.AppendAllText(
                Path.Combine(AppPaths.DataRoot, "translation.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [Translate] {message}{Environment.NewLine}");
        }
        catch
        {
            // Best-effort file logging; never let logging crash the translator.
        }
    }

    public event Action<string>? TranslationChanged;
    public event Action<string>? StatusChanged;

    public bool IsConnected => _session.IsLoaded;
    public bool IsConnecting => _session.IsLoading;
    public string StatusText => _session.StatusText;
    public BackendKind Backend => _backend;

    /// <summary>True when the native .litertlm model is present on disk.</summary>
    public bool IsNativeModelAvailable => TranslationModelInfo.IsDownloaded;

    /// <summary>Target language as a code ("ru") or a name ("Russian"); the prompt always gets the name.</summary>
    public void SetTargetLanguage(string language)
    {
        var name = TranslationLanguages.NameFor(language);
        if (name is null)
            return;

        if (!string.Equals(_session.TargetLanguage, name, StringComparison.Ordinal))
        {
            _session.TargetLanguage = name;
            Log($"target language -> {name}");
        }
    }

    /// <summary>The recognizer's language ("auto"/null = let the model detect the source).</summary>
    public void SetSourceLanguage(string? languageCode) =>
        _session.SourceLanguage = TranslationLanguages.NameFor(languageCode);

    /// <summary>
    /// Sets an optional extra system prompt appended to the built-in translation
    /// instruction. Changing it re-establishes the engine so the new prompt takes
    /// effect on the next translation.
    /// </summary>
    public void SetAdditionalSystemPrompt(string? prompt)
    {
        var trimmed = prompt?.Trim() ?? "";
        if (string.Equals(_additionalSystemPrompt, trimmed, StringComparison.Ordinal))
            return;

        _additionalSystemPrompt = trimmed;
        ResetEngine();
    }

    /// <summary>
    /// Switches the translation engine (native/http). Drops the active translator;
    /// the next translation re-establishes it (loads the model or probes the server).
    /// </summary>
    public void UpdateBackend(BackendKind backend)
    {
        if (_backend == backend)
            return;

        var previous = _backend;
        _backend = backend;
        ResetEngine();
        Log($"backend switched -> {backend} (was {previous})");
        StatusChanged?.Invoke(backend == BackendKind.Native
            ? "Translation engine: native (in-process)"
            : "Translation engine: HTTP server");
    }

    /// <summary>
    /// Selects the compute backend for the native engine: "cpu" (XNNPACK) or
    /// "gpu" (WebGPU delegate). Drops the active native engine so the change
    /// takes effect on the next translation.
    /// </summary>
    public void SetComputeBackend(string backend)
    {
        var normalized = (backend ?? "cpu").Trim().ToLowerInvariant();
        if (normalized is not ("cpu" or "gpu"))
            normalized = "cpu";

        if (_computeBackend == normalized)
            return;

        _computeBackend = normalized;
        if (_activeEngine == BackendKind.Native)
            ResetEngine();

        Log($"compute backend -> {normalized}");
    }

    /// <summary>
    /// Switches to a different LiteRT-LM server (HTTP engine). Drops the current
    /// connection state; the next translation re-probes the new endpoint.
    /// </summary>
    public void UpdateServerUrl(string baseUrl)
    {
        var trimmed = baseUrl?.Trim() ?? "";
        if (trimmed.Length == 0
            || string.Equals(_serverUrl, trimmed, StringComparison.OrdinalIgnoreCase))
            return;

        _serverUrl = trimmed;
        if (_activeEngine == BackendKind.Http)
            ResetEngine();
        StatusChanged?.Invoke("Translation server changed");
    }

    /// <summary>Releases the engine once the decode in flight finishes; the next translation reconnects.</summary>
    private void ResetEngine() => _ = ResetEngineAsync();

    private async Task ResetEngineAsync()
    {
        try
        {
            await _session.ReplaceTranslatorAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log($"engine reset failed: {ex.Message}");
        }
    }

    public void Feed(string fullText) => _session.Feed(fullText);

    /// <summary>Skips the text already in the transcript: only speech after this point is translated.</summary>
    public void StartFrom(string fullText) => _session.StartFrom(fullText);

    /// <summary>Clears the translation and translates the whole transcript, then keeps following it.</summary>
    public Task TranslateAllAsync(string fullText, CancellationToken cancellationToken = default) =>
        _session.TranslateAllAsync(fullText, cancellationToken);

    public Task FlushAsync(CancellationToken cancellationToken = default) => _session.FlushAsync(cancellationToken);

    public void Reset() => _session.Reset();

    public Task EnsureConnectedAsync(CancellationToken cancellationToken = default) =>
        _session.EnsureLoadedAsync(cancellationToken);

    private async Task<ITextTranslator> CreateTranslatorAsync(CancellationToken cancellationToken)
    {
        // Native engine (preferred): in-process, offline, no sidecar. Falls back to the
        // HTTP server when the model is not downloaded.
        if (_backend == BackendKind.Native && TranslationModelInfo.IsDownloaded)
            return await ConnectNativeAsync(cancellationToken).ConfigureAwait(false);

        return await ConnectHttpAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ITextTranslator> ConnectNativeAsync(CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke("Loading translation model (native)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var backend = _computeBackend;
        Log($"connecting native: modelPath={TranslationModelInfo.LocalModelPath}, backend={backend}");
        var translator = await Task.Run(() => (ITextTranslator)new LiteRTLmNativeTranslator(new LiteRTLmNativeOptions
        {
            ModelPath = TranslationModelInfo.LocalModelPath,
            Backend = backend,
            LogLevel = LiteRTLmLogLevel.Warning,
            MaxTokens = _baseOptions.MaxTokens,
            AdditionalSystemPrompt = _additionalSystemPrompt,
        }), cancellationToken).ConfigureAwait(false);

        _activeEngine = BackendKind.Native;
        Log($"native connected in {sw.ElapsedMilliseconds} ms");
        return translator;
    }

    private async Task<ITextTranslator> ConnectHttpAsync(CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(_backend == BackendKind.Native && !TranslationModelInfo.IsDownloaded
            ? "Model not downloaded — falling back to translation server..."
            : "Connecting to translation server...");

        Log("connecting http: no native model or http backend forced — using server");
        var translator = new LiteRTLmTranslator(new LiteRTLmOptions
        {
            BaseUrl = _serverUrl,
            Endpoint = _baseOptions.Endpoint,
            Model = _baseOptions.Model,
            Temperature = _baseOptions.Temperature,
            MaxTokens = _baseOptions.MaxTokens,
            AdditionalSystemPrompt = _additionalSystemPrompt,
        });

        // Cheap reachability probe: a reachable server answers, an unreachable one
        // throws and the session records the failure in its status.
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(TimeSpan.FromSeconds(4));
            await translator.TranslateAsync("ok", _session.TargetLanguage, null, probe.Token).ConfigureAwait(false);
            Log($"http connected at {_serverUrl} in {sw.ElapsedMilliseconds} ms");
        }
        catch
        {
            translator.Dispose();
            throw;
        }

        _activeEngine = BackendKind.Http;
        return translator;
    }

    public void Dispose()
    {
        // Waits (bounded) for the decode in flight: native decode cannot abort mid-token.
        try
        {
            _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log($"dispose: {ex.Message}");
        }
    }
}
