using System.Text;
using SpeechLib;
using SpeechLib.LiteRT;
using SpeechLib.Translation;

namespace VoiceType.Uno.Services;

/// <summary>
/// Cross-platform live translation for the streaming transcript. Ports the
/// VoiceType.WinUI TranslationService behavior onto the HTTP LiteRT-LM backend
/// (<see cref="LiteRTLmTranslator"/>), which is pure managed code and therefore
/// works on Linux — unlike the native LiteRT-LM backend (Windows-only NuGet).
///
/// Complete sentences are translated and finalized immediately; the unfinished
/// tail is translated as a cancellable "draft" re-run as new words arrive, with
/// successive drafts diffed so the stable word-aligned prefix locks in place.
/// </summary>
public sealed class TranslationService : IDisposable
{
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
    private HttpClient? _http;
    private ITextTranslator? _translator;
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

    public TranslationService(LiteRTLmOptions options)
    {
        _baseOptions = options;
        _serverUrl = options.BaseUrl;
    }

    public event Action<string>? TranslationChanged;
    public event Action<string>? StatusChanged;

    public bool IsConnected => _isConnected;
    public bool IsConnecting => _isConnecting;
    public string StatusText => _statusText;

    public void SetTargetLanguage(string language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            _targetLanguage = language;
    }

    /// <summary>
    /// Switches to a different LiteRT-LM server. Drops the current connection
    /// state; the next translation re-probes the new endpoint.
    /// </summary>
    public void UpdateServerUrl(string baseUrl)
    {
        var trimmed = baseUrl?.Trim() ?? "";
        if (trimmed.Length == 0
            || string.Equals(_serverUrl, trimmed, StringComparison.OrdinalIgnoreCase))
            return;

        _serverUrl = trimmed;
        (_translator as IDisposable)?.Dispose();
        _translator = null;
        _http?.Dispose();
        _http = null;
        _isConnected = false;
        SetStatus("Translation server changed");
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
    /// Establishes the connection to the LiteRT-LM server. The HTTP backend is
    /// stateless, so "loading" is a cheap reachability check against the server.
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
            SetStatus("Connecting to translation server...");

            try
            {
                _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                _translator ??= new LiteRTLmTranslator(new LiteRTLmOptions
                {
                    BaseUrl = _serverUrl,
                    Endpoint = _baseOptions.Endpoint,
                    Model = _baseOptions.Model,
                    Temperature = _baseOptions.Temperature,
                    MaxTokens = _baseOptions.MaxTokens
                });

                // Cheap reachability probe: translate an empty-ish payload. A
                // reachable server answers (even with an empty translation);
                // an unreachable one throws, which flips the status to offline.
                using var probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(TimeSpan.FromSeconds(4));
                await _translator.TranslateAsync("ok", _targetLanguage, null, probe.Token)
                    .ConfigureAwait(false);

                _isConnected = true;
                SetStatus("Translation ready");
            }
            catch (Exception ex)
            {
                _isConnected = false;
                SetStatus($"Translation server offline: {ex.Message}");
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
