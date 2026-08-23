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
    private string _draftDecodedSource = "";
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

            // When the recognizer omits punctuation for a long stretch, force-finalize
            // bounded chunks so translation keeps progressing instead of stalling.
            while (_buffer.Length - _consumed > MaxTailChars)
            {
                var chunk = CutForceChunkLocked();
                if (chunk.Length == 0)
                    break;
                sentences.Add(chunk);
            }
        }

        // A completed sentence supersedes the in-flight draft for the old tail:
        // cancel it synchronously so the final translation grabs the gate promptly.
        if (sentences.Count > 0)
        {
            _draftCts?.Cancel();
            _draftSource = "";
        }

        foreach (var sentence in sentences)
            EnqueueFinal(sentence);

        ScheduleDraft();
    }

    /// <summary>
    /// Splits an oversized unpunctuated tail at the last word boundary before
    /// <see cref="MaxTailChars"/> and advances <see cref="_consumed"/> past it.
    /// Must be called under <c>_bufferLock</c>.
    /// </summary>
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
            splitAt = tailStart + MaxTailChars; // no usable word boundary — cut hard

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
            _draftDecodedSource = "";
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
    /// Schedules draft translation of the unfinished tail. A single draft loop keeps
    /// re-translating the tail as it grows, streaming tokens to the display on the fly
    /// and locking the stable word-aligned prefix after each completed pass.
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

        if (tail == _draftSource && _draftTask is { IsCompleted: false })
            return; // the running loop already owns this tail

        _draftSource = tail;

        if (_draftTask is { IsCompleted: false } && _draftCts is { IsCancellationRequested: false })
            return; // the live loop will observe the updated tail on its next pass

        // Start (or restart) the draft loop.
        _draftCts?.Cancel();
        var cts = new CancellationTokenSource();
        _draftCts = cts;
        _draftTask = RunDraftLoopAsync(cts.Token);
    }

    private async Task RunDraftLoopAsync(CancellationToken ct)
    {
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

        while (!ct.IsCancellationRequested)
        {
            var source = _draftSource;
            if (source.Length == 0)
                break;

            // Coalesce bursts of partial updates before spending a decode pass.
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
                break; // caught up — a later Feed restarts the loop

            if (!await TryEnterTranslateGateAsync(ct).ConfigureAwait(false))
                break;

            try
            {
                var current = _draftSource;
                if (current != source || current.Length == 0)
                    continue; // changed while waiting for the gate — retry with latest

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

    /// <summary>
    /// Streams a draft translation token-by-token into the provisional display and,
    /// on completion, locks the stable word-aligned prefix. Returns <c>false</c> when
    /// the draft was superseded before finishing (a newer tail now owns the display).
    /// </summary>
    private async Task<bool> StreamDraftAsync(LiteRTLmNativeTranslator translator, string source, CancellationToken ct)
    {
        var partial = new StringBuilder();
        await foreach (var token in translator
            .TranslateStreamAsync(source, _targetLanguage, cancellationToken: ct)
            .ConfigureAwait(false))
        {
            partial.Append(token);
            if (ct.IsCancellationRequested || source != _draftSource)
                return false; // superseded — a newer pass owns the display

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
