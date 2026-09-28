using System.Text;

namespace SpeechLib.Translation;

/// <summary>Tuning knobs for <see cref="LiveTranslationSession"/>.</summary>
public sealed class LiveTranslationOptions
{
    /// <summary>Quiet time after a transcript update before the unfinished tail is re-translated.</summary>
    public int DraftDebounceMs { get; init; } = 150;

    /// <summary>Words two successive drafts must share before their common prefix is shown as stable.</summary>
    public int MinStableWords { get; init; } = 2;

    /// <summary>Unpunctuated tail longer than this is force-finalized in word-aligned chunks.</summary>
    public int MaxTailChars { get; init; } = 160;

    /// <summary>Shortest force-finalized chunk; below this the cut falls back to a hard cut.</summary>
    public int MinForceChunkChars { get; init; } = 40;

    /// <summary>Sentence → translation memo size (repeated phrases skip the decoder).</summary>
    public int MemoCapacity { get; init; } = 256;

    /// <summary>Offer the previous sentence and its translation as context to the backend.</summary>
    public bool UseContext { get; init; } = true;

    /// <summary>Status reported before the first load.</summary>
    public string InitialStatus { get; init; } = "Translation off";
}

/// <summary>
/// Translates a streaming transcript incrementally, in sync with the recognizer.
/// Complete sentences are translated and finalized; the unfinished tail is translated
/// as a cancellable "draft" that is re-run whenever new words arrive, and successive
/// drafts are diffed so the stable word-aligned prefix stays put while only the
/// divergent suffix keeps changing.
/// </summary>
/// <remarks>
/// <para>Shared by every application head so they behave identically. The application
/// supplies the backend through a factory (in-process LiteRT-LM, an HTTP server, …),
/// decides when the model is available, and marshals the events to its UI thread.</para>
/// <para><see cref="Feed"/>, <see cref="FlushAsync"/> and <see cref="Reset"/> are expected on
/// one thread (the UI thread); translation runs on the thread pool and the events are
/// raised from worker threads.</para>
/// <para>Text fed to the session may be <em>revised</em>, not only appended: streaming
/// recognizers rewrite their last words as more audio arrives. A revision inside the
/// unfinished tail restarts the draft from the changed word; a revision inside a
/// sentence that was already translated is ignored (finals are not recalled).</para>
/// </remarks>
public sealed class LiveTranslationSession : IAsyncDisposable
{
    private readonly LiveTranslationOptions _options;
    private Func<CancellationToken, Task<ITextTranslator>> _translatorFactory;

    private readonly object _stateLock = new();
    private readonly object _bufferLock = new();
    private readonly StringBuilder _completed = new();
    private readonly SemaphoreSlim _translateGate = new(1, 1);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly object _inflightLock = new();
    private readonly List<Task> _inflight = new();

    // Monotonic session counter. Reset()/DisposeAsync() increment it so in-flight
    // finals from a previous session detect they are stale and must not write.
    private long _generation;

    private ITextTranslator? _translator;
    private volatile string _targetLanguage = "Russian";
    private volatile string? _sourceLanguage;
    private volatile string _statusText;
    private volatile bool _isLoaded;
    private volatile bool _isLoading;

    // Provisional display state (guarded by _stateLock): `_locked + _streaming`.
    private string _locked = "";
    private string _streaming = "";

    // Draft (unfinished tail) state. `_draftSource` is volatile so worker threads can
    // detect superseded drafts without taking the lock.
    private volatile string _draftSource = "";
    private string _draftCompletedSource = "";
    private string _draftPrevFull = "";
    private string _draftDecodedSource = "";
    private CancellationTokenSource? _draftCts;
    private Task? _draftTask;

    // Transcript state (guarded by _bufferLock). Positions are counted in WORDS, not
    // characters: streaming recognizers rewrite their tail (casing, punctuation, a
    // revised word), and a word anchor survives those rewrites where a character
    // offset would split the transcript in the wrong place and re-translate it.
    private string _fedText = "";
    private int _anchorWords;                        // words already handled (finalized or skipped)
    private string _tail = "";                       // text after the anchor (the draft source)
    private List<string> _pendingSentences = new();  // complete sentences seen in the previous feed

    // Context for the backend: the last finalized sentence and its translation.
    private string _lastFinalSource = "";
    private string _lastFinalTranslation = "";

    // Memo of finalized translations, keyed by target language + normalized sentence.
    private readonly Dictionary<string, string> _memo = new(StringComparer.Ordinal);
    private readonly Queue<string> _memoOrder = new();

    public LiveTranslationSession(
        Func<CancellationToken, Task<ITextTranslator>> translatorFactory,
        LiveTranslationOptions? options = null)
    {
        _translatorFactory = translatorFactory ?? throw new ArgumentNullException(nameof(translatorFactory));
        _options = options ?? new LiveTranslationOptions();
        _statusText = _options.InitialStatus;
    }

    /// <summary>Raised (on a worker thread) with the full cumulative translated text.</summary>
    public event Action<string>? TranslationChanged;

    /// <summary>Raised (on a worker thread) with a human-readable status message.</summary>
    public event Action<string>? StatusChanged;

    public bool IsLoaded => _isLoaded;
    public bool IsLoading => _isLoading;
    public string StatusText => _statusText;

    /// <summary>Target language name for subsequently enqueued sentences ("Russian").</summary>
    public string TargetLanguage
    {
        get => _targetLanguage;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                _targetLanguage = value.Trim();
        }
    }

    /// <summary>Source language name when the recognizer language is fixed; null = let the model detect it.</summary>
    public string? SourceLanguage
    {
        get => _sourceLanguage;
        set => _sourceLanguage = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The text a viewer sees: finalized sentences, then the locked and streaming draft.</summary>
    public string DisplayText
    {
        get
        {
            lock (_stateLock)
                return ComposeDisplayLocked();
        }
    }

    /// <summary>
    /// Feeds the current full transcript. Only text after the anchor is translated;
    /// see <see cref="StartFrom"/> to skip what is already on screen.
    /// </summary>
    /// <remarks>
    /// A sentence is finalized only when (a) a word follows it and (b) it came out
    /// identically in the previous feed. Streaming recognizers end their revisable
    /// preview with a period ("…the first time.") and rewrite it a moment later;
    /// finalizing on the first period made every revision a new sentence and the
    /// translation repeated itself. The tail stays a revisable draft until it settles.
    /// </remarks>
    public void Feed(string fullText)
    {
        fullText ??= "";

        var finals = new List<string>();
        bool tailChanged;
        lock (_bufferLock)
        {
            if (fullText == _fedText)
                return;
            _fedText = fullText;

            var words = WordSpans(fullText);
            if (_anchorWords > words.Count)
                _anchorWords = words.Count;

            // Complete sentences after the anchor that already appeared, unchanged, in
            // the previous feed are stable: finalize them.
            var sentences = CompleteSentences(fullText, words, _anchorWords, requireFollowingWord: true);
            var stable = 0;
            while (stable < sentences.Count && stable < _pendingSentences.Count
                   && sentences[stable].Text == _pendingSentences[stable])
            {
                finals.Add(sentences[stable].Text);
                _anchorWords = sentences[stable].EndWord;
                stable++;
            }
            _pendingSentences = sentences.Skip(stable).Select(s => s.Text).ToList();

            // When the recognizer omits punctuation for a long stretch, finalize bounded
            // word-aligned chunks so translation keeps progressing. The newest words
            // stay out of the chunk: they are the ones a recognizer still revises.
            while (SpanLength(words, _anchorWords, words.Count) > _options.MaxTailChars
                   && words.Count - _anchorWords > ForceChunkKeepWords)
            {
                var end = _anchorWords + 1;
                while (end < words.Count - ForceChunkKeepWords
                       && SpanLength(words, _anchorWords, end + 1) <= _options.MaxTailChars)
                    end++;

                finals.Add(Slice(fullText, words, _anchorWords, end));
                _anchorWords = end;
                _pendingSentences.Clear();
            }

            var tail = Slice(fullText, words, _anchorWords, words.Count);
            tailChanged = tail != _tail;
            _tail = tail;
        }

        if (!tailChanged && finals.Count == 0)
            return;

        // A completed sentence supersedes the in-flight draft for the old tail:
        // cancel it synchronously so the final translation grabs the gate promptly.
        if (finals.Count > 0)
        {
            _draftCts?.Cancel();
            _draftSource = "";
        }

        foreach (var sentence in finals)
            EnqueueFinal(sentence);

        ScheduleDraft();
    }

    /// <summary>
    /// Starts translating from the end of <paramref name="fullText"/>: what is already
    /// in the transcript is skipped, later speech is translated. Used when translation
    /// is switched on mid-session; <see cref="TranslateAllAsync"/> translates everything.
    /// </summary>
    public void StartFrom(string fullText)
    {
        fullText ??= "";
        _draftCts?.Cancel();
        _draftSource = "";
        lock (_bufferLock)
        {
            _fedText = fullText;
            _anchorWords = WordSpans(fullText).Count;
            _pendingSentences.Clear();
            _tail = "";
        }
    }

    /// <summary>Clears the output and translates the whole of <paramref name="fullText"/>, then keeps following it.</summary>
    public async Task TranslateAllAsync(string fullText, CancellationToken cancellationToken = default)
    {
        Reset();
        Feed(fullText ?? "");
        await FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Words kept out of a force-finalized chunk (still subject to revision).</summary>
    private const int ForceChunkKeepWords = 4;

    private readonly record struct WordSpan(int Start, int End);

    private readonly record struct Sentence(string Text, int EndWord);

    private static List<WordSpan> WordSpans(string text)
    {
        var spans = new List<WordSpan>();
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i >= text.Length)
                break;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
                i++;
            spans.Add(new WordSpan(start, i));
        }
        return spans;
    }

    private static string Slice(string text, List<WordSpan> words, int from, int to) =>
        from >= to ? "" : text[words[from].Start..words[to - 1].End];

    private static int SpanLength(List<WordSpan> words, int from, int to) =>
        from >= to ? 0 : words[to - 1].End - words[from].Start;

    /// <summary>True when the word closes a sentence ("medium.", "right?"", "done…").</summary>
    private static bool EndsSentence(string text, WordSpan word)
    {
        var end = word.End;
        while (end > word.Start && text[end - 1] is '"' or '\'' or ')' or ']' or '»' or '”')
            end--;
        return end > word.Start && text[end - 1] is '.' or '!' or '?' or '…' or '。' or '！' or '？';
    }

    /// <summary>
    /// Sentences starting at word <paramref name="from"/>. With <paramref name="requireFollowingWord"/>
    /// the last sentence counts only when another word follows it; otherwise the
    /// unterminated remainder is returned as a final sentence too (flush).
    /// </summary>
    private static List<Sentence> CompleteSentences(string text, List<WordSpan> words, int from, bool requireFollowingWord)
    {
        var sentences = new List<Sentence>();
        var start = from;
        for (var i = from; i < words.Count; i++)
        {
            if (!EndsSentence(text, words[i]))
                continue;
            if (requireFollowingWord && i + 1 >= words.Count)
                break;
            sentences.Add(new Sentence(Slice(text, words, start, i + 1), i + 1));
            start = i + 1;
        }

        if (!requireFollowingWord && start < words.Count)
            sentences.Add(new Sentence(Slice(text, words, start, words.Count), words.Count));

        return sentences;
    }

    /// <summary>Translates the remaining tail as final and waits for all in-flight work.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _draftCts?.Cancel();
        _draftSource = "";
        _draftDecodedSource = "";

        List<Sentence> remaining;
        lock (_bufferLock)
        {
            var words = WordSpans(_fedText);
            remaining = CompleteSentences(_fedText, words, Math.Min(_anchorWords, words.Count), requireFollowingWord: false);
            _anchorWords = words.Count;
            _pendingSentences.Clear();
            _tail = "";
        }

        foreach (var sentence in remaining)
            EnqueueFinal(sentence.Text);

        await WaitForInflightAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears the transcript buffer, the translated output and the sentence context.</summary>
    public void Reset()
    {
        Interlocked.Increment(ref _generation);
        _draftCts?.Cancel();
        _draftSource = "";

        lock (_bufferLock)
        {
            _fedText = "";
            _anchorWords = 0;
            _pendingSentences.Clear();
            _tail = "";
        }

        lock (_stateLock)
        {
            _completed.Clear();
            _locked = "";
            _streaming = "";
            _draftPrevFull = "";
            _draftCompletedSource = "";
            _draftDecodedSource = "";
            _lastFinalSource = "";
            _lastFinalTranslation = "";
        }

        RaiseTranslationChanged();
    }

    /// <summary>
    /// Creates the backend through the factory (no-op when already loaded). Returns
    /// when the backend is ready or the failure has been recorded in <see cref="StatusText"/>.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded)
            return;

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isLoaded || _translator is not null)
                return;

            _isLoading = true;
            SetStatus("Loading translation model...");
            try
            {
                _translator = await _translatorFactory(cancellationToken).ConfigureAwait(false);
                _isLoaded = true;
                SetStatus("Translation model ready");
            }
            catch (OperationCanceledException)
            {
                SetStatus(_options.InitialStatus);
                throw;
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

    /// <summary>
    /// Drops the loaded backend (waiting for the decode in flight, which native engines
    /// cannot abort mid-token) and optionally installs a new factory. The next
    /// translation reloads lazily.
    /// </summary>
    public async Task ReplaceTranslatorAsync(
        Func<CancellationToken, Task<ITextTranslator>>? translatorFactory = null,
        CancellationToken cancellationToken = default)
    {
        _draftCts?.Cancel();
        _draftSource = "";
        _draftDecodedSource = "";

        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (translatorFactory is not null)
                _translatorFactory = translatorFactory;

            var gateHeld = await TryEnterTranslateGateAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _translator?.Dispose();
                _translator = null;
                _isLoaded = false;
            }
            finally
            {
                if (gateHeld)
                    ExitTranslateGate();
            }
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        _draftCts?.Cancel();

        // Let in-flight decodes wind down before disposing the backend; force-dispose
        // after a bounded grace period because native decode is not cancellable mid-token.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await WaitForInflightAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort shutdown.
        }

        _translator?.Dispose();
        _translator = null;
        _translateGate.Dispose();
        _loadGate.Dispose();
    }

    private async Task WaitForInflightAsync(CancellationToken cancellationToken)
    {
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
            // Best-effort: remaining translations stay unfinished.
        }
    }

    // ── Finals ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Finalizes a complete sentence: a memo hit or a draft that already translated this
    /// exact tail is promoted without decoding; otherwise the sentence is translated
    /// and streamed into the provisional area before being committed.
    /// </summary>
    private void EnqueueFinal(string sentence)
    {
        // Captured now so a language change mid-flight does not re-target a queued sentence.
        var generation = Interlocked.Read(ref _generation);
        var request = BuildRequest(sentence);

        string? promoted = null;
        lock (_stateLock)
        {
            if (TranslationGuards.IsAlreadyInTarget(sentence, request.TargetLanguage))
            {
                promoted = sentence; // already in the target language: pass through
            }
            else if (_memo.TryGetValue(MemoKey(request.TargetLanguage, sentence), out var cached))
            {
                promoted = cached;
            }
            else if (_draftCompletedSource == NormalizeTail(sentence) && _draftPrevFull.Length > 0)
            {
                promoted = _draftPrevFull;
                _draftCompletedSource = "";
                _draftPrevFull = "";
            }
        }

        var task = promoted is not null
            ? CommitPromotedAsync(sentence, promoted, generation)
            : TranslateFinalAsync(sentence, request, generation);

        lock (_inflightLock)
        {
            _inflight.RemoveAll(t => t.IsCompleted);
            _inflight.Add(task);
        }
    }

    private async Task CommitPromotedAsync(string sentence, string text, long generation)
    {
        if (!await TryEnterTranslateGateAsync().ConfigureAwait(false))
            return;
        try
        {
            if (Interlocked.Read(ref _generation) != generation)
                return; // session was reset/disposed while queued

            ClearProvisional();
            Commit(sentence, text);
            RaiseTranslationChanged();
        }
        finally
        {
            ExitTranslateGate();
        }
    }

    private async Task TranslateFinalAsync(string sentence, TranslationRequest request, long generation)
    {
        try
        {
            await EnsureLoadedAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var translator = _translator;
        if (translator is null)
            return;

        if (!await TryEnterTranslateGateAsync().ConfigureAwait(false))
            return;
        try
        {
            if (Interlocked.Read(ref _generation) != generation)
                return; // stale — do not touch the new session's display

            string previous;
            lock (_stateLock)
            {
                previous = _locked + _streaming;
                _locked = "";
                _streaming = previous;
            }

            var partial = new StringBuilder();
            await foreach (var token in translator.TranslateStreamAsync(request).ConfigureAwait(false))
            {
                if (Interlocked.Read(ref _generation) != generation)
                    return; // session reset mid-decode; bail without committing

                partial.Append(token);
                var shown = TranslationGuards.StripContextEcho(partial.ToString(), request, final: false);
                lock (_stateLock)
                    _streaming = MergeProvisional(previous, shown);
                RaiseTranslationChanged();
            }

            if (Interlocked.Read(ref _generation) != generation)
                return;

            var result = TranslationGuards.StripContextEcho(
                TranslationOutputCleaner.Clean(partial.ToString()), request, final: true);

            // A decode that ran away (usually by continuing the context) is retried once
            // without context; context is a quality aid, never worth a wrong line.
            if (TranslationGuards.IsRunaway(result, sentence) && request.HasContext)
            {
                var plain = request with { PreviousSource = null, PreviousTranslation = null };
                result = TranslationOutputCleaner.Clean(await translator.TranslateAsync(plain).ConfigureAwait(false));
                if (Interlocked.Read(ref _generation) != generation)
                    return;
            }

            if (result.Length > 0)
                Commit(sentence, result);

            lock (_stateLock)
                _streaming = "";
            RaiseTranslationChanged();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by Reset/Dispose — state is left for the next pass.
        }
        catch (Exception ex)
        {
            if (Interlocked.Read(ref _generation) != generation)
                return;

            lock (_stateLock)
                _streaming = "";
            SetStatus($"Translation error: {ex.Message}");
        }
        finally
        {
            ExitTranslateGate();
        }
    }

    /// <summary>Appends a finalized translation, remembers it, and makes it the context for the next sentence.</summary>
    private void Commit(string sentence, string translation)
    {
        lock (_stateLock)
        {
            if (_completed.Length > 0)
                _completed.AppendLine();
            _completed.Append(translation);

            _lastFinalSource = sentence;
            _lastFinalTranslation = translation;
            Memoize(MemoKey(_targetLanguage, sentence), translation);
        }
    }

    private TranslationRequest BuildRequest(string text)
    {
        string? previousSource = null, previousTranslation = null;
        if (_options.UseContext)
        {
            lock (_stateLock)
            {
                previousSource = _lastFinalSource;
                previousTranslation = _lastFinalTranslation;
            }
        }

        return new TranslationRequest(text, _targetLanguage)
        {
            SourceLanguage = _sourceLanguage,
            PreviousSource = string.IsNullOrEmpty(previousSource) ? null : previousSource,
            PreviousTranslation = string.IsNullOrEmpty(previousTranslation) ? null : previousTranslation,
        };
    }

    private static string MemoKey(string language, string sentence) =>
        language + "\n" + NormalizeTail(sentence).ToLowerInvariant();

    /// <summary>Must be called under <c>_stateLock</c>.</summary>
    private void Memoize(string key, string translation)
    {
        if (_options.MemoCapacity <= 0)
            return;

        if (_memo.TryAdd(key, translation))
        {
            _memoOrder.Enqueue(key);
            while (_memoOrder.Count > _options.MemoCapacity)
                _memo.Remove(_memoOrder.Dequeue());
        }
        else
        {
            _memo[key] = translation;
        }
    }

    // ── Drafts ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Schedules draft translation of the unfinished tail. A single loop keeps
    /// re-translating the tail as it grows, streaming tokens to the display and locking
    /// the stable word-aligned prefix after each completed pass.
    /// </summary>
    private void ScheduleDraft()
    {
        string tail;
        lock (_bufferLock)
            tail = _tail.Trim();

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
            return; // the live loop observes the updated tail on its next pass

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
                await Task.Delay(_options.DraftDebounceMs, ct).ConfigureAwait(false);
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
                    continue; // changed while waiting for the gate — retry with the latest

                var completed = await StreamDraftAsync(translator, current, ct).ConfigureAwait(false);
                if (completed)
                    _draftDecodedSource = current;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                SetStatus($"Translation error: {ex.Message}");
                break;
            }
            finally
            {
                ExitTranslateGate();
            }
        }
    }

    /// <summary>
    /// Streams a draft translation into the provisional display and, on completion,
    /// locks the stable word-aligned prefix. A pass runs to completion even when the
    /// tail keeps growing underneath it (streaming recognizers revise their tail several
    /// times per second, so an aborting pass would never finish and nothing would be
    /// shown until the sentence ends); the loop then re-runs with the latest tail.
    /// Only a finalized sentence or a reset cancels a pass. Returns true when the pass
    /// translated the current tail, false when a newer tail is already waiting.
    /// </summary>
    private async Task<bool> StreamDraftAsync(ITextTranslator translator, string source, CancellationToken ct)
    {
        string previous;
        lock (_stateLock)
        {
            previous = _locked + _streaming;
            _locked = "";
            _streaming = previous;
        }

        var request = BuildRequest(source);
        string full;
        if (TranslationGuards.IsAlreadyInTarget(source, request.TargetLanguage))
        {
            full = source; // already in the target language: no decode
        }
        else
        {
            var partial = new StringBuilder();
            await foreach (var token in translator.TranslateStreamAsync(request, ct).ConfigureAwait(false))
            {
                partial.Append(token);
                if (ct.IsCancellationRequested)
                    return false;

                var shown = TranslationGuards.StripContextEcho(partial.ToString(), request, final: false);
                lock (_stateLock)
                    _streaming = MergeProvisional(previous, shown);
                RaiseTranslationChanged();
            }

            full = TranslationGuards.StripContextEcho(TranslationOutputCleaner.Clean(partial.ToString()), request, final: true);
            if (TranslationGuards.IsRunaway(full, source))
                full = ""; // drafts are disposable; the final pass decides
        }
        var current = false;
        lock (_stateLock)
        {
            if (!ct.IsCancellationRequested)
            {
                var locked = StablePrefix.LongestWordAlignedCommonPrefix(_draftPrevFull, full, _options.MinStableWords);
                _draftPrevFull = full;
                _draftCompletedSource = NormalizeTail(source);
                _locked = locked;
                _streaming = full.Length >= locked.Length ? full[locked.Length..] : "";
                current = source == _draftSource;
            }
        }

        if (!ct.IsCancellationRequested)
            RaiseTranslationChanged();

        return current;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private void ClearProvisional()
    {
        lock (_stateLock)
        {
            _locked = "";
            _streaming = "";
        }
    }

    /// <summary>
    /// Merges a freshly streamed partial with the previously displayed provisional text
    /// so the live text updates incrementally instead of blinking back to empty on each
    /// re-run: when one is a prefix of the other the longer is shown, otherwise the new
    /// text wins.
    /// </summary>
    internal static string MergeProvisional(string previous, string current)
    {
        if (previous.Length == 0)
            return current;

        var max = Math.Min(previous.Length, current.Length);
        var common = 0;
        while (common < max && previous[common] == current[common])
            common++;
        if (common == previous.Length || common == current.Length)
            return current.Length >= previous.Length ? current : previous;

        return current;
    }

    internal static string NormalizeTail(string text)
    {
        var t = text.Trim();
        var end = t.Length;
        while (end > 0 && t[end - 1] is '.' or '!' or '?' or '…')
            end--;
        return t[..end].TrimEnd();
    }

    private string ComposeDisplayLocked()
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
        return sb.ToString();
    }

    private void RaiseTranslationChanged()
    {
        string text;
        lock (_stateLock)
            text = ComposeDisplayLocked();
        TranslationChanged?.Invoke(text);
    }

    private void SetStatus(string status)
    {
        _statusText = status;
        StatusChanged?.Invoke(status);
    }
}
