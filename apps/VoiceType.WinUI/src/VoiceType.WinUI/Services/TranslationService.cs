using System.Text;
using SpeechLib.LiteRT.Native;
using SpeechLib.Translation;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services;

/// <summary>
/// Buffers the streaming transcript and translates it incrementally, in sync with the
/// recognizer. Complete sentences are translated and finalized immediately, while the
/// unfinished tail is translated as a cancellable "draft" that is re-run whenever new
/// words arrive. Successive draft outputs are diffed so that the stable, word-aligned
/// prefix is locked in place while only the divergent suffix stays provisional.
/// </summary>
/// <remarks>
/// <see cref="Feed"/>, <see cref="FlushAsync"/>, and <see cref="Reset"/> are expected to be
/// called from the UI thread. Translation work runs on the thread pool; the
/// <c>TranslationChanged</c>/<c>StatusChanged</c> events are raised from worker threads.
/// </remarks>
public sealed class TranslationService : ITranslationService
{
    private const int DraftDebounceMs = 300;
    private const int MinStableWords = 2;

    private readonly object _stateLock = new();
    private readonly object _bufferLock = new();
    private readonly StringBuilder _buffer = new();
    private readonly StringBuilder _completed = new();
    private readonly SemaphoreSlim _translateGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _inflightLock = new();
    private readonly List<Task> _inflight = new();

    private LiteRTLmNativeTranslator? _translator;
    private volatile string _targetLanguage = "ru";
    private volatile string _statusText = "Translation off";
    private volatile bool _isLoaded;
    private volatile bool _isLoading;

    // Provisional display state, guarded by _stateLock. The displayed provisional
    // translation is always `_locked + _streaming`; `_locked` is the stable prefix.
    private string _locked = "";
    private string _streaming = "";

    // Draft (unfinished tail) translation state. `_draftSource` is volatile so
    // worker threads can detect superseded drafts without taking the lock.
    private volatile string _draftSource = "";
    private string _draftCompletedSource = "";
    private string _draftPrevFull = "";
    private CancellationTokenSource? _draftCts;
    private Task? _draftTask;

    // Sentence buffer state, guarded by _bufferLock.
    private int _consumed;
    private int _fedLength;

    public event Action<string>? TranslationChanged;
    public event Action<string>? StatusChanged;

    public bool IsModelAvailable => TranslationModelInfo.IsDownloaded;
    public bool IsLoaded => _isLoaded;
    public bool IsLoading => _isLoading;
    public string StatusText => _statusText;

    public void SetTargetLanguage(string language)
    {
        if (!string.IsNullOrWhiteSpace(language))
            _targetLanguage = language;
    }

    public void Feed(string fullText)
    {
        if (string.IsNullOrEmpty(fullText))
            return;

        List<string> sentences;
        lock (_bufferLock)
        {
            var start = Math.Clamp(_fedLength, 0, fullText.Length);
            var delta = fullText[start..];
            _fedLength = fullText.Length;

            if (delta.Length == 0)
                return;

            _buffer.Append(delta);
            sentences = SentenceSplitter.ExtractCompleteSentences(_buffer, ref _consumed);
        }

        foreach (var sentence in sentences)
            EnqueueFinal(sentence);

        ScheduleDraft();
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _draftCts?.Cancel();
        _draftSource = "";

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
            // Best-effort flush: cancellation leaves the remaining translations unfinished.
        }
    }

    public void Reset()
    {
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
        }

        RaiseTranslationChanged();
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded || !IsModelAvailable)
            return;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isLoaded || _translator is not null)
                return;

            if (!IsModelAvailable)
            {
                SetStatus("Translation model not downloaded");
                return;
            }

            _isLoading = true;
            SetStatus("Loading translation model...");

            try
            {
                _translator = await Task.Run(() =>
                {
                    var options = new LiteRTLmNativeOptions
                    {
                        ModelPath = TranslationModelInfo.LocalModelPath,
                        Backend = TranslationModelInfo.Backend,
                        LogLevel = LiteRTLmLogLevel.Silent,
                    };
                    return new LiteRTLmNativeTranslator(options);
                }, cancellationToken).ConfigureAwait(false);

                _isLoaded = true;
                SetStatus("Translation model ready");
            }
            catch (Exception ex)
            {
                _translator?.Dispose();
                _translator = null;
                _isLoaded = false;
                SetStatus($"Translation model error: {ex.Message}");
            }
            finally
            {
                _isLoading = false;
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _draftCts?.Cancel();

        _translator?.Dispose();
        _translator = null;
        _translateGate.Dispose();
        _loadGate.Dispose();

        // Give in-flight tasks a moment to observe the disposed state; they hold
        // their own references and will fail gracefully on the disposed engine.
        await Task.CompletedTask.ConfigureAwait(false);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    /// <summary>
    /// Finalizes a complete sentence. When a draft already translated this exact
    /// tail, its output is promoted without re-decoding (zero-latency lock); otherwise
    /// the sentence is re-translated and streamed into the provisional area.
    /// </summary>
    private void EnqueueFinal(string sentence)
    {
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
            ? CommitPromotedAsync(promoted)
            : TranslateFinalAsync(sentence, _targetLanguage);

        lock (_inflightLock)
        {
            _inflight.RemoveAll(t => t.IsCompleted);
            _inflight.Add(task);
        }
    }

    private async Task CommitPromotedAsync(string text)
    {
        if (!await TryEnterTranslateGateAsync().ConfigureAwait(false))
            return;
        try
        {
            ClearProvisional();
            AppendCompleted(text);
            RaiseTranslationChanged();
        }
        finally
        {
            ExitTranslateGate();
        }
    }

    private async Task TranslateFinalAsync(string sentence, string language)
    {
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetStatus($"Translation model failed to load: {ex.Message}");
            return;
        }

        var translator = _translator;
        if (translator is null)
            return;

        // A completed sentence supersedes any in-flight draft for the same span.
        _draftCts?.Cancel();

        if (!await TryEnterTranslateGateAsync().ConfigureAwait(false))
            return;
        try
        {
            ClearProvisional();

            var partial = new StringBuilder();
            await foreach (var token in translator.TranslateStreamAsync(sentence, language).ConfigureAwait(false))
            {
                partial.Append(token);
                lock (_stateLock)
                {
                    _locked = "";
                    _streaming = partial.ToString();
                }
                RaiseTranslationChanged();
            }

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
            // Cancelled by Reset/Dispose — state is left for the next pass.
        }
        catch (Exception ex)
        {
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

    /// <summary>
    /// Schedules a debounced, cancellable draft translation of the unfinished tail.
    /// The latest tail always wins: an older draft is cancelled so its decode stops
    /// and the newer one can start.
    /// </summary>
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

        if (tail == _draftSource)
            return;

        _draftSource = tail;
        _draftCts?.Cancel();
        var cts = new CancellationTokenSource();
        _draftCts = cts;
        _draftTask = RunDraftAsync(tail, cts.Token);
    }

    private async Task RunDraftAsync(string source, CancellationToken ct)
    {
        try
        {
            await Task.Delay(DraftDebounceMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            if (_translator is null)
                await EnsureLoadedAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            SetStatus($"Translation model failed to load: {ex.Message}");
            return;
        }

        var translator = _translator;
        if (translator is null)
            return;

        if (!await TryEnterTranslateGateAsync(ct).ConfigureAwait(false))
            return;
        try
        {
            if (ct.IsCancellationRequested || source != _draftSource)
                return;

            // Decode into a local buffer without touching the display, so a stale
            // draft never flashes through the UI (double buffering).
            var partial = new StringBuilder();
            await foreach (var token in translator
                .TranslateStreamAsync(source, _targetLanguage, cancellationToken: ct)
                .ConfigureAwait(false))
            {
                partial.Append(token);
            }

            var full = partial.ToString().Trim();
            bool stale;
            lock (_stateLock)
            {
                stale = ct.IsCancellationRequested || source != _draftSource;
                if (!stale)
                {
                    var locked = StablePrefix.LongestWordAlignedCommonPrefix(_draftPrevFull, full, MinStableWords);
                    _draftPrevFull = full;
                    _draftCompletedSource = NormalizeTail(source);
                    _locked = locked;
                    _streaming = full.Length >= locked.Length ? full[locked.Length..] : "";
                }
            }

            if (stale)
                return;

            RaiseTranslationChanged();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer draft or a final translation — they own the display.
        }
        catch (Exception ex)
        {
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

    /// <summary>Acquires the translation gate, tolerating cancellation and shutdown.</summary>
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
            // Shutdown: the gate was disposed while a task held it.
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
