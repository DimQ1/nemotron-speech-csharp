using System.Text;
using SpeechLib;
using SpeechLib.LiteRT;
using SpeechLib.LiteRT.Native;
using SpeechLib.Translation;

namespace VoiceType.Uno.Services;

/// <summary>
/// Cross-platform live translation for the streaming transcript. Ports the
/// VoiceType.WinUI TranslationService behavior with two interchangeable engines:
///
///   native — in-process LiteRT-LM (<see cref="LiteRTLmNativeTranslator"/>); the
///     .litertlm model runs in the same process via LiteRtLmSharp natives, which
///     ship for win-x64 and linux-x64, so there is no sidecar/server on Linux.
///   http   — external LiteRT-LM server (<see cref="LiteRTLmTranslator"/>) over an
///     OpenAI-compatible endpoint; used as the fallback when the native model
///     has not been downloaded.
///
/// Complete sentences are translated and finalized immediately; the unfinished
/// tail is translated as a cancellable "draft" re-run as new words arrive, with
/// successive drafts diffed so the stable word-aligned prefix locks in place.
/// </summary>
public sealed class TranslationService : IDisposable
{
    public enum BackendKind { Native, Http }

    private const int DraftDebounceMs = 150;
    private const int MinStableWords = 2;
    private const int MaxTailChars = 160;
    private const int MinForceChunkChars = 40;

    private readonly object _stateLock = new();
    private readonly object _bufferLock = new();
    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _completed = new();
    private readonly SemaphoreSlim _translateGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _inflightLock = new();
    private readonly List<Task> _inflight = new();

    private long _generation;

    private readonly LiteRTLmOptions _baseOptions;
    private volatile string _serverUrl;
    private volatile BackendKind _backend = BackendKind.Native;
    private HttpClient? _http;
    private ITextTranslator? _translator;
    private BackendKind _activeEngine;
    private volatile string _targetLanguage = "ru";
    private volatile string _statusText = "Translation off";
    private volatile bool _isConnected;
    private volatile bool _isConnecting;

    private string _locked = "";
    private string _streaming = "";

    private volatile string _draftSource = "";
    private string _draftCompletedSource = "";
    private string _draftPrevFull = "";
    private string _draftDecodedSource = "";
    private CancellationTokenSource? _draftCts;
    private Task? _draftTask;

    private int _consumed;
    private int _fedLength;

    public TranslationService(LiteRTLmOptions options, BackendKind backend = BackendKind.Native)
    {
        _baseOptions = options;
        _serverUrl = options.BaseUrl;
        _backend = backend;
    }

    public event Action<string>? TranslationChanged;
    public event Action<string>? StatusChanged;

    public bool IsConnected => _isConnected;
    public bool IsConnecting => _isConnecting;
    public string StatusText => _statusText;
    public BackendKind Backend => _backend;

    /// <summary>True when the native .litertlm model is present on disk.</summary>
    public bool IsNativeModelAvailable => TranslationModelInfo.IsDownloaded;

    public void SetTargetLanguage(string language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            _targetLanguage = language;
    }

    /// <summary>
    /// Switches the translation engine (native/http). Drops the active translator;
    /// the next translation re-establishes it (loads the model or probes the server).
    /// </summary>
    public void UpdateBackend(BackendKind backend)
    {
        if (_backend == backend)
            return;

        _backend = backend;
        ResetEngine();
        SetStatus(backend == BackendKind.Native
            ? "Translation engine: native (in-process)"
            : "Translation engine: HTTP server");
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
        SetStatus("Translation server changed");
    }

    private void ResetEngine()
    {
        (_translator as IDisposable)?.Dispose();
        _translator = null;
        _http?.Dispose();
        _http = null;
        _isConnected = false;
    }

    public void Feed(string fullText)
    {
        if (string.IsNullOrEmpty(fullText))
            return;

        List<string> sentences;
        lock (_bufferLock)
        {
            if (fullText.Length < _fedLength)
            {
                _buffer.Clear();
                _consumed = 0;
                _fedLength = 0;
                _draftSource = "";
            }

            var start = Math.Clamp(_fedLength, 0, fullText.Length);
            var delta = fullText[start..];
            _fedLength = fullText.Length;

            if (delta.Length == 0)
                return;

            _buffer.Append(delta);
            sentences = SentenceSplitter.ExtractCompleteSentences(_buffer, ref _consumed);

            while (_buffer.Length - _consumed > MaxTailChars)
            {
                var chunk = CutForceChunkLocked();
                if (chunk.Length == 0)
                    break;
                sentences.Add(chunk);
            }
        }

        if (sentences.Count > 0)
        {
            _draftCts?.Cancel();
            _draftSource = "";
        }

        foreach (var sentence in sentences)
            EnqueueFinal(sentence);

        ScheduleDraft();
    }

    private string CutForceChunkLocked()
    {
        var tailStart = _consumed;
        var tailLen = _buffer.Length - tailStart;
        if (tailLen <= MaxTailChars)
            return "";

        var splitAt = tailStart + MaxTailChars;
        while (splitAt > tailStart && !char.IsWhiteSpace(_buffer[splitAt - 1]))
            splitAt--;

        if (splitAt - tailStart < MinForceChunkChars)
            splitAt = tailStart + MaxTailChars;

        var chunk = _buffer.ToString(tailStart, splitAt - tailStart).Trim();
        var k = splitAt;
        while (k < _buffer.Length && char.IsWhiteSpace(_buffer[k]))
            k++;
        _consumed = k;
        return chunk;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _draftCts?.Cancel();
        _draftSource = "";
        _draftDecodedSource = "";

        string tail;
        lock (_bufferLock)
        {
            tail = _buffer.ToString(_consumed, _buffer.Length - _consumed).Trim();
            _buffer.Clear();
            _consumed = 0;
            _fedLength = 0;
        }

        if (tail.Length > 0)
            EnqueueFinal(tail);

        Task[] pending;
        lock (_inflightLock)
        {
            pending = _inflight.Where(t => !t.IsCompleted).ToArray();
            _inflight.Clear();
        }

        var draft = _draftTask;
        var all = draft is { IsCompleted: false } ? pending.Append(draft).ToArray() : pending;
        if (all.Length == 0)
            return;

        try
        {
            await Task.WhenAll(all).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        _draftCts?.Cancel();
        _draftSource = "";

        lock (_bufferLock)
        {
            _buffer.Clear();
            _consumed = 0;
            _fedLength = 0;
        }

        lock (_stateLock)
        {
            _completed.Clear();
            _locked = "";
            _streaming = "";
            _draftPrevFull = "";
            _draftCompletedSource = "";
            _draftDecodedSource = "";
        }

        RaiseTranslationChanged();
    }

    /// <summary>
    /// Establishes the active translation engine. Native loads the .litertlm
    /// model in-process (no sidecar); when the model is not downloaded it falls
    /// back to the HTTP server, which needs only a reachability probe.
    /// </summary>
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected)
            return;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isConnected && _translator is not null)
                return;

            _isConnecting = true;
            try
            {
                // Native engine (preferred): in-process, offline, no sidecar.
                // Falls back to the HTTP server when the model is not downloaded.
                if (_backend == BackendKind.Native && TranslationModelInfo.IsDownloaded)
                {
                    await ConnectNativeAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await ConnectHttpAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _isConnected = false;
                SetStatus($"Translation unavailable: {ex.Message}");
            }
            finally
            {
                _isConnecting = false;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task ConnectNativeAsync(CancellationToken cancellationToken)
    {
        SetStatus("Loading translation model (native)...");
        _translator = await Task.Run(() => (ITextTranslator)new LiteRTLmNativeTranslator(new LiteRTLmNativeOptions
        {
            ModelPath = TranslationModelInfo.LocalModelPath,
            Backend = TranslationModelInfo.Backend,
            LogLevel = LiteRTLmLogLevel.Warning,
            MaxTokens = _baseOptions.MaxTokens
        }), cancellationToken).ConfigureAwait(false);

        _activeEngine = BackendKind.Native;
        _isConnected = true;
        SetStatus("Translation ready (native)");
    }

    private async Task ConnectHttpAsync(CancellationToken cancellationToken)
    {
        SetStatus(_backend == BackendKind.Native && !TranslationModelInfo.IsDownloaded
            ? "Model not downloaded — falling back to translation server..."
            : "Connecting to translation server...");

        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        _translator = new LiteRTLmTranslator(new LiteRTLmOptions
        {
            BaseUrl = _serverUrl,
            Endpoint = _baseOptions.Endpoint,
            Model = _baseOptions.Model,
            Temperature = _baseOptions.Temperature,
            MaxTokens = _baseOptions.MaxTokens
        });

        // Cheap reachability probe: translate an empty-ish payload. A reachable
        // server answers; an unreachable one throws, flipping status to offline.
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(TimeSpan.FromSeconds(4));
        await _translator.TranslateAsync("ok", _targetLanguage, null, probe.Token)
            .ConfigureAwait(false);

        _activeEngine = BackendKind.Http;
        _isConnected = true;
        SetStatus("Translation ready (server)");
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _generation);
        _draftCts?.Cancel();
        (_translator as IDisposable)?.Dispose();
        _translator = null;
        _http?.Dispose();
        _http = null;
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void EnqueueFinal(string sentence)
    {
        var generation = Interlocked.Read(ref _generation);
        var language = _targetLanguage;

        string? promoted = null;
        lock (_stateLock)
        {
            if (_draftCompletedSource == NormalizeTail(sentence) && _draftPrevFull.Length > 0)
            {
                promoted = _draftPrevFull;
                _draftCompletedSource = "";
                _draftPrevFull = "";
            }
        }

        var task = promoted is not null
            ? CommitPromotedAsync(promoted, generation)
            : TranslateFinalAsync(sentence, language, generation);

        lock (_inflightLock)
        {
            _inflight.RemoveAll(t => t.IsCompleted);
            _inflight.Add(task);
        }
    }

    private async Task CommitPromotedAsync(string text, long generation)
    {
        if (!await TryEnterTranslateGateAsync().ConfigureAwait(false))
            return;
        try
        {
            if (Interlocked.Read(ref _generation) != generation)
                return;

            ClearProvisional();
            AppendCompleted(text);
            RaiseTranslationChanged();
        }
        finally
        {
            ExitTranslateGate();
        }
    }

    private async Task TranslateFinalAsync(string sentence, string language, long generation)
    {
        try
        {
            await EnsureConnectedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref _generation) == generation)
                SetStatus($"Translation connect failed: {ex.Message}");
            return;
        }

        var translator = _translator;
        if (translator is null || !_isConnected)
            return;

        if (!await TryEnterTranslateGateAsync().ConfigureAwait(false))
            return;
        try
        {
            if (Interlocked.Read(ref _generation) != generation)
                return;

            ClearProvisional();

            var partial = new StringBuilder();
            await foreach (var token in translator.TranslateStreamAsync(sentence, language).ConfigureAwait(false))
            {
                if (Interlocked.Read(ref _generation) != generation)
                    return;

                partial.Append(token);
                lock (_stateLock)
                {
                    _locked = "";
                    _streaming = partial.ToString();
                }
                RaiseTranslationChanged();
            }

            if (Interlocked.Read(ref _generation) != generation)
                return;

            var result = partial.ToString().Trim();
            if (result.Length > 0)
                AppendCompleted(result);

            lock (_stateLock)
            {
                _streaming = "";
            }
            RaiseTranslationChanged();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref _generation) != generation)
                return;

            lock (_stateLock)
            {
                _streaming = "";
            }
            SetStatus($"Translation error: {ex.Message}");
        }
        finally
        {
            ExitTranslateGate();
        }
    }

    private void ScheduleDraft()
    {
        string tail;
        lock (_bufferLock)
        {
            tail = _buffer.ToString(_consumed, _buffer.Length - _consumed).Trim();
        }

        if (tail.Length == 0)
        {
            _draftCts?.Cancel();
            _draftSource = "";
            return;
        }

        if (tail == _draftSource && _draftTask is { IsCompleted: false })
            return;

        _draftSource = tail;

        if (_draftTask is { IsCompleted: false } && _draftCts is { IsCancellationRequested: false })
            return;

        _draftCts?.Cancel();
        var cts = new CancellationTokenSource();
        _draftCts = cts;
        _draftTask = RunDraftLoopAsync(cts.Token);
    }

    private async Task RunDraftLoopAsync(CancellationToken ct)
    {
        try
        {
            if (_translator is null || !_isConnected)
                await EnsureConnectedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetStatus($"Translation connect failed: {ex.Message}");
            return;
        }

        var translator = _translator;
        if (translator is null || !_isConnected)
            return;

        while (!ct.IsCancellationRequested)
        {
            var source = _draftSource;
            if (source.Length == 0)
                break;

            try
            {
                await Task.Delay(DraftDebounceMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            source = _draftSource;
            if (source.Length == 0 || ct.IsCancellationRequested)
                break;

            if (source == _draftDecodedSource)
                break;

            if (!await TryEnterTranslateGateAsync(ct).ConfigureAwait(false))
                break;

            try
            {
                var current = _draftSource;
                if (current != source || current.Length == 0)
                    continue;

                var completed = await StreamDraftAsync(translator, current, ct).ConfigureAwait(false);
                if (completed)
                    _draftDecodedSource = current;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                ExitTranslateGate();
            }
        }
    }

    private async Task<bool> StreamDraftAsync(ITextTranslator translator, string source, CancellationToken ct)
    {
        var partial = new StringBuilder();
        await foreach (var token in translator
            .TranslateStreamAsync(source, _targetLanguage, cancellationToken: ct)
            .ConfigureAwait(false))
        {
            partial.Append(token);
            if (ct.IsCancellationRequested || source != _draftSource)
                return false;

            lock (_stateLock)
            {
                _locked = "";
                _streaming = partial.ToString();
            }
            RaiseTranslationChanged();
        }

        var full = partial.ToString().Trim();
        var committed = false;
        lock (_stateLock)
        {
            if (!ct.IsCancellationRequested && source == _draftSource)
            {
                var locked = StablePrefix.LongestWordAlignedCommonPrefix(_draftPrevFull, full, MinStableWords);
                _draftPrevFull = full;
                _draftCompletedSource = NormalizeTail(source);
                _locked = locked;
                _streaming = full.Length >= locked.Length ? full[locked.Length..] : "";
                committed = true;
            }
        }

        if (committed)
            RaiseTranslationChanged();

        return committed;
    }

    private async Task<bool> TryEnterTranslateGateAsync(CancellationToken ct = default)
    {
        try
        {
            await _translateGate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void ExitTranslateGate()
    {
        try
        {
            _translateGate.Release();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void AppendCompleted(string text)
    {
        lock (_stateLock)
        {
            if (_completed.Length > 0)
                _completed.AppendLine();
            _completed.Append(text);
        }
    }

    private void ClearProvisional()
    {
        lock (_stateLock)
        {
            _locked = "";
            _streaming = "";
        }
    }

    private static string NormalizeTail(string text)
    {
        var t = text.Trim();
        int end = t.Length;
        while (end > 0 && t[end - 1] is '.' or '!' or '?' or '…')
            end--;
        return t[..end].TrimEnd();
    }

    private void RaiseTranslationChanged()
    {
        string text;
        lock (_stateLock)
        {
            var sb = new StringBuilder();
            if (_completed.Length > 0)
            {
                sb.Append(_completed);
                if (_locked.Length + _streaming.Length > 0)
                    sb.AppendLine();
            }
            sb.Append(_locked);
            sb.Append(_streaming);
            text = sb.ToString();
        }

        TranslationChanged?.Invoke(text);
    }

    private void SetStatus(string status)
    {
        _statusText = status;
        StatusChanged?.Invoke(status);
    }
}
