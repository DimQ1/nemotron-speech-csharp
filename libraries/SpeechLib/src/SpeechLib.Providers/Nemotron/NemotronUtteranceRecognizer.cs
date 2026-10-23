using System.Text;
using System.Text.RegularExpressions;
using SpeechLib.Audio;
using SpeechLib.Models;

namespace SpeechLib.Nemotron;

/// <summary>
/// Utterances and word times on top of <see cref="NemotronRnntRecognizer"/>: the apps' utterance interface
/// (<see cref="IUtteranceStreamingRecognizer"/>, <see cref="ITimedUtteranceRecognizer"/>) for the cache-aware streaming
/// transducer, over the same plain-ONNX-Runtime engine and with the same text.
/// </summary>
/// <remarks>
/// <para><b>Tokens are final.</b> The transducer never revises what it emitted, so <c>Partial</c> is the committed text of
/// the open utterance, <c>Stable</c> is the same text (nothing is revisable), and a <c>Final</c> is the text of an utterance
/// once it ended (the plain interface's <see cref="PartialText"/> is null: nothing is provisional). Partial and Stable contain no language tags; <c>Final</c> keeps the closing tag
/// unless <see cref="NemotronUtteranceOptions.KeepLanguageTagInFinal"/> is false (the apps read the detected language from
/// it and strip it themselves). Leading and trailing blanks are trimmed. The text of all Finals followed by the last
/// Partial, with tags removed and blanks collapsed, equals the text of the plain recognizer.</para>
/// <para><b>An utterance ends</b> (i) when a language tag follows letters (the tag is emitted after the terminal punctuation
/// when the next speech begins or a pause has lasted a while: median 0.64 s, 90th percentile 2.4 s after the last word on
/// the TTS fixtures), (ii) when <see cref="StopHistoryEouSeconds"/> passed without a token since the last letter token,
/// measured as a difference of frame positions on the stream timeline (chunks the silence gate dropped are silence), (iii)
/// in front of the next word once the utterance is <see cref="NemotronUtteranceOptions.MaxUtteranceSeconds"/> long, (iv) at
/// <see cref="FlushUtterance"/>. All rules look at the stream only, so the segmentation does not depend on how the audio
/// was batched; only the call in which a Final is reported does. A Final holds letters, never only marks, except the one a flush
/// commits for a mark that no word followed (a continuation, see below): after <see cref="FlushUtterance"/> nothing is left open.</para>
/// <para><b>An utterance can continue the previous one.</b> The model writes the terminal punctuation and the tag when it hears
/// the next speech (or, in about a fifth of the cases where the speaker simply stops, within a second). When a silence closed
/// the utterance first, the mark and the tag lead the next utterance (<c>". &lt;en-US&gt; You are so rude"</c>; on the TTS fixtures
/// 37 to 42 % of the pauses of 1.04 s or more). They are neither dropped nor waited for (a wait would have to last as long as
/// the pause). The same happens, in 0.2 % of the words of real speech, to the last piece of a word that the model emitted
/// more than the silence rule after the first ones (<c>"... able"</c> then <c>"s are ..."</c>). A Final whose first piece
/// carries no word marker is reported with <see cref="StreamingResult.FinalContinuesPrevious"/>; join texts with
/// <see cref="UtteranceText.Join(string?, string?, bool)"/>, which then leaves out the space (the text is exactly the plain
/// recognizer's). The mark's word in <c>FinalWords</c> sits zero-width on the start of the first word, so a segment starts
/// where the speech does.</para>
/// <para><b>Word times</b> (<see cref="StreamingResult.FinalWords"/> / <see cref="StreamingResult.PartialWords"/>, seconds on the
/// absolute clock of the samples fed since the last reset, silence-dropped chunks included) come from the emission frames of
/// the tokens: calibrated lag per chunk size and language, then snapped to the speech onsets and offsets of the audio
/// around pauses, clamped to the audio fed. <see cref="NemotronWordTimingOptions"/> explains and overrides every number.
/// Flush padding stays on the engine's chunk grid; the clock subtracts it again, so a stream that continues after a flush
/// keeps its times. One <c>WordTiming</c> per whitespace word of the tag-free Final text. Words never overlap, also across
/// utterances: the first words of an utterance start where the last word of the one before ends at the earliest.</para>
/// <para>Not thread-safe, like the engine.</para>
/// </remarks>
public sealed class NemotronUtteranceRecognizer : ITimedUtteranceRecognizer, ILanguageConfigurable, IRuntimeConfigurable
{
    private static readonly Regex MultipleSpaces = new(" {2,}", RegexOptions.Compiled);

    private readonly NemotronRnntRecognizer _engine;
    private readonly bool _ownsEngine;
    private readonly Func<NemotronRnntRecognizer>? _createOfflineEngine;
    private readonly NemotronUtteranceOptions _options;
    private readonly NemotronWordTimer _timer;
    private readonly NemotronEnergyTracker? _tracker;
    private AutomaticGainControl? _gain;
    private readonly int _eouFrames;
    private readonly int _capFrames;
    private readonly bool[] _lexical;
    private NemotronUtteranceRecognizer? _offline;
    private bool _disposed;

    // open utterance
    private readonly List<NemotronToken> _open = new();
    private int _openLetterTokens;
    private long _firstLetterFrame = -1;
    private long _lastLetterFrame = -1;
    private string? _openLanguage;

    // results waiting for the caller, and what the stream remembers
    private readonly List<FinishedUtterance> _finished = new();
    private string? _lastTag;
    private long _fedSamples;
    private int _lastTokenCount;
    private readonly List<(int FirstChunk, long CumulativeSamples)> _paddings = new();
    private double _lastWordEnd;       // end of the last word of the last finished utterance: the next utterance starts behind it

    private sealed record FinishedUtterance(string Text, List<WordTiming> Words, bool ContinuesPrevious);

    /// <summary>Loads a Nemotron model folder (see <see cref="NemotronModelConfig.IsNemotronModel"/>).</summary>
    /// <param name="modelPath">Model folder with <c>genai_config.json</c>, the three graphs and <c>vocab.txt</c>.</param>
    /// <param name="executionProvider">
    /// <c>cpu</c> (default) or <c>webgpu[:hp|:lp]</c>: WebGPU runs the encoder on a discrete graphics card, everything else and every request
    /// that cannot be carried out is the CPU (see <see cref="ExecutionProvider"/>). The second engine of <see cref="TranscribeWords"/> runs on
    /// the CPU while this one holds WebGPU: only one WebGPU recognizer per process is allowed.
    /// </param>
    /// <param name="langId">Language code or id; null keeps lang_id 0 (en-US).</param>
    /// <param name="useVad">Start with the engine's silence drop rule enabled (dropped chunks keep their place on the clock).</param>
    /// <param name="engineOptions">Threads, blank penalty, clean front-end.</param>
    /// <param name="options">Utterance and word-timing parameters.</param>
    public NemotronUtteranceRecognizer(string modelPath, string executionProvider = "cpu", string? langId = null, bool useVad = false,
        NemotronRecognizerOptions? engineOptions = null, NemotronUtteranceOptions? options = null)
        : this(new NemotronRnntRecognizer(modelPath, executionProvider, langId, useVad, engineOptions), options ?? new NemotronUtteranceOptions(),
            () => new NemotronRnntRecognizer(modelPath, executionProvider, langId, useVad: false, engineOptions))
    {
    }

    /// <summary>Test seam and composition: a recognizer over an engine it owns.</summary>
    /// <param name="engine">The streaming engine.</param>
    /// <param name="options">Utterance and word-timing parameters.</param>
    /// <param name="createOfflineEngine">Creates the independent engine <see cref="TranscribeWords"/> runs on; null makes it use a reset of <paramref name="engine"/>.</param>
    /// <param name="ownsEngine">Dispose <paramref name="engine"/> with this object (false when tests share a cached engine).</param>
    internal NemotronUtteranceRecognizer(NemotronRnntRecognizer engine, NemotronUtteranceOptions options, Func<NemotronRnntRecognizer>? createOfflineEngine = null,
        bool ownsEngine = true)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _ownsEngine = ownsEngine;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _createOfflineEngine = createOfflineEngine;
        if (options.StopHistoryEouSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), options.StopHistoryEouSeconds, "StopHistoryEouSeconds must be positive.");

        var frameSeconds = (double)engine.SamplesPerFrame / engine.SampleRate;
        _eouFrames = Math.Max(1, (int)Math.Round(options.StopHistoryEouSeconds / frameSeconds));
        _capFrames = options.MaxUtteranceSeconds > 0 ? Math.Max(1, (int)Math.Round(options.MaxUtteranceSeconds / frameSeconds)) : int.MaxValue;

        var vocabulary = engine.Vocabulary;
        _lexical = new bool[vocabulary.Count];
        for (var id = 0; id < _lexical.Length; id++)
            _lexical[id] = vocabulary.PieceOf(id).Any(char.IsLetterOrDigit);

        _timer = new NemotronWordTimer(vocabulary, options.WordTiming, engine.SamplesPerFrame, engine.SampleRate, engine.ChunkSamples);
        if (options.WordTiming.Snap is { } snap)
            _tracker = new NemotronEnergyTracker(snap.HistorySeconds, engine.SampleRate);
        if (options.AutoGain)
            _gain = new AutomaticGainControl(engine.SampleRate);
    }

    // ── identity and settings ───────────────────────────────────────────

    /// <inheritdoc />
    public int SampleRate => _engine.SampleRate;

    /// <inheritdoc />
    public int ChunkSamples => _engine.ChunkSamples;

    /// <inheritdoc />
    public double StopHistoryEouSeconds => _options.StopHistoryEouSeconds;

    /// <summary>Longest utterance in seconds; 0 when uncapped.</summary>
    public double MaxUtteranceSeconds => _options.MaxUtteranceSeconds > 0 ? _options.MaxUtteranceSeconds : 0;

    /// <summary>The effective parameters.</summary>
    public NemotronUtteranceOptions Options => _options;

    /// <inheritdoc />
    public int LastTokenCount => _lastTokenCount;

    /// <summary>The language tag (<c>"ru-RU"</c>) of the last <c>&lt;xx-XX&gt;</c> token of the stream, or null.</summary>
    public string? LastLanguageTag => _lastTag;

    /// <summary>True when the model has no language input (a single-language export).</summary>
    public bool IsSingleLanguage => _engine.IsSingleLanguage;

    /// <summary><c>"true"</c> while the silence drop rule is active, else <c>"false"</c>.</summary>
    public string VadStatus => _engine.VadStatus;

    /// <summary>Where the graphs run and why (the encoder on CPU or WebGPU, the reason when WebGPU was requested but not used); see <see cref="NemotronProviderInfo"/>.</summary>
    public NemotronProviderInfo ExecutionProvider => _engine.ExecutionProvider;

    /// <summary>The streaming engine underneath (diagnostics and tests).</summary>
    internal NemotronRnntRecognizer Engine => _engine;

    /// <inheritdoc />
    public bool TrySetLanguage(string language) => _engine.TrySetLanguage(language);

    /// <inheritdoc />
    public bool TrySetVad(bool enabled) => _engine.TrySetVad(enabled);

    /// <inheritdoc />
    public bool TrySetSearchOptions(int numBeams, double repetitionPenalty) => _engine.TrySetSearchOptions(numBeams, repetitionPenalty);

    // ── plain streaming interface ───────────────────────────────────────

    /// <summary>
    /// Feeds audio and returns the engine's text delta (with language tags, like the plain recognizer), or null when no chunk
    /// went through the model. The utterance bookkeeping advances; utterances that end during the call are not reported.
    /// </summary>
    public string? ProcessAudio(float[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ThrowIfDisposed();
        var text = Feed(chunk);
        _finished.Clear();
        return text;
    }

    /// <summary>Flushes the engine (zero-padded last chunk) and returns its text; the open utterance is closed when it holds letters.</summary>
    public string? Flush()
    {
        ThrowIfDisposed();
        var text = FlushEngine();
        FinishOpen();
        _finished.Clear();
        return text;
    }

    /// <summary>
    /// Always null: the plain interface's provisional text is text that is not yet in the <see cref="ProcessAudio"/> deltas, and
    /// every token here is final the moment a call returns it. The open utterance is <see cref="StreamingResult.Partial"/>.
    /// </summary>
    public string? PartialText => null;

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ThrowIfDisposed();
        _engine.ResetStreamingState();
        _tracker?.Reset();
        _open.Clear();
        _openLetterTokens = 0;
        _firstLetterFrame = _lastLetterFrame = -1;
        _openLanguage = null;
        _finished.Clear();
        _lastTag = null;
        _fedSamples = 0;
        _lastTokenCount = 0;
        _paddings.Clear();
        _lastWordEnd = 0;
    }

    // ── utterance interface ─────────────────────────────────────────────

    /// <inheritdoc />
    public StreamingResult ProcessUtterance(float[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ThrowIfDisposed();
        Feed(chunk);
        return TakeResult();
    }

    /// <inheritdoc />
    public StreamingResult FlushUtterance()
    {
        ThrowIfDisposed();
        FlushEngine();
        FinishOpen(endOfStream: true);
        return TakeResult();
    }

    /// <summary>
    /// The words of a complete waveform, independent of the streaming state: what a new recognizer with the same model, language
    /// and settings reports for the audio as one stream with a flush at the end, joined the way the transcript is (the closing
    /// mark of a sentence and the late rest of a word belong to the word before, so the words are those of the text).
    /// </summary>
    /// <remarks>
    /// A stream that is in progress, or ended without <see cref="ResetStreamingState"/>, is left alone: the words come from a
    /// second engine (loaded on first use: about a second and the memory of the model once more, the encoder keeps its streaming
    /// caches inside the engine). While nothing has been fed since the last reset the engine itself is used and left reset; call
    /// <see cref="ResetStreamingState"/> before to avoid the second engine. The gain control starts afresh for every call and the
    /// level of the stream is not touched, so the same waveform always gives the same words, which are the ones a stream of a
    /// new recognizer reports. Without a way to create a second engine (test seam) the streaming state is reset before and after
    /// the call.
    /// </remarks>
    public IReadOnlyList<WordTiming> TranscribeWords(float[] waveform)
    {
        ArgumentNullException.ThrowIfNull(waveform);
        ThrowIfDisposed();

        NemotronUtteranceRecognizer worker;
        if (_createOfflineEngine is null || IsIdle)
        {
            ResetStreamingState();
            worker = this;
        }
        else
        {
            _offline ??= new NemotronUtteranceRecognizer(_createOfflineEngine(), _options);
            worker = _offline;
            worker._engine.TrySetLanguage(_engine.LanguageId.ToString());
            worker._engine.BlankPenalty = _engine.BlankPenalty;
            worker._engine.TrySetVad(_engine.VadEnabled);
            worker.ResetStreamingState();
        }

        var words = new List<WordTiming>();
        void Add(StreamingResult result)
        {
            if (result.FinalWords is not { Count: > 0 } ready)
                return;
            if (result.FinalContinuesPrevious && words.Count > 0)
                AttachWords(words, ready);
            else
                words.AddRange(ready);
        }

        var streamGain = worker._gain;
        worker._gain = _options.AutoGain ? new AutomaticGainControl(SampleRate) : null;
        try
        {
            var step = worker.ChunkSamples;
            for (var position = 0; position < waveform.Length; position += step)
            {
                var count = Math.Min(step, waveform.Length - position);
                var piece = new float[count];
                Array.Copy(waveform, position, piece, 0, count);
                Add(worker.ProcessUtterance(piece));
            }
            Add(worker.FlushUtterance());
        }
        finally
        {
            worker._gain = streamGain;
            worker.ResetStreamingState();
        }
        return words;
    }

    // Nothing has been fed since the last reset: the engine holds no stream.
    private bool IsIdle => _fedSamples == 0 && _engine.ChunksConsumed == 0 && _engine.BufferedSamples == 0;

    // ── feeding ─────────────────────────────────────────────────────────

    // Feeds the engine in pieces that complete at most one chunk each and handles the tokens of a chunk before the next one
    // starts, so the energy history, the stream position and the utterance state a Final is built from are those of the
    // chunk that ended the utterance, however the caller batched the audio.
    private string? Feed(ReadOnlySpan<float> samples)
    {
        if (_gain is not null)
            samples = _gain.Process(samples);
        _lastTokenCount = 0;
        StringBuilder? text = null;
        var encoded = false;
        var needed = _engine.ChunkSamples + _engine.Engine.LookaheadSamples;
        var offset = 0;
        while (offset < samples.Length)
        {
            var take = Math.Min(samples.Length - offset, Math.Max(1, needed - _engine.BufferedSamples));
            var piece = samples.Slice(offset, take);
            offset += take;
            _tracker?.Append(piece);
            _fedSamples += take;

            var delta = _engine.ProcessAudio(piece);
            if (delta is not null)
            {
                encoded = true;
                (text ??= new StringBuilder()).Append(delta);
            }
            _lastTokenCount += _engine.LastTokenCount;
            Absorb();
        }
        return encoded ? text?.ToString() ?? "" : null;
    }

    private string? FlushEngine()
    {
        _lastTokenCount = 0;
        var chunk = _engine.ChunkSamples;
        var remainder = _engine.BufferedSamples % chunk;
        var text = _engine.Flush();
        _lastTokenCount = _engine.LastTokenCount;

        if (remainder > 0)
        {
            // the padded zeros are on the engine's grid but were never fed: later chunks sit that much earlier on the clock
            var total = (_paddings.Count > 0 ? _paddings[^1].CumulativeSamples : 0) + (chunk - remainder);
            _paddings.Add((_engine.ChunksConsumed, total));
        }
        Absorb();
        return text;
    }

    private long PaddingBefore(int chunk)
    {
        for (var i = _paddings.Count - 1; i >= 0; i--)
            if (chunk >= _paddings[i].FirstChunk)
                return _paddings[i].CumulativeSamples;
        return 0;
    }

    // ── utterance rules ─────────────────────────────────────────────────

    private void Absorb()
    {
        var tokens = _engine.LastTokens;
        var framesPerChunk = _engine.FramesPerChunk;
        foreach (var chunk in _engine.LastChunks)
        {
            for (var k = 0; k < chunk.TokenCount; k++)
                AddToken(tokens[chunk.FirstToken + k]);

            // end of the chunk: how long has it been since the last letter?
            if (_openLetterTokens > 0 && (long)(chunk.Index + 1) * framesPerChunk - _lastLetterFrame - 1 >= _eouFrames)
                FinishOpen();
        }
    }

    private void AddToken(NemotronToken token)
    {
        var vocabulary = _engine.Vocabulary;
        if (_openLetterTokens > 0)
        {
            if (token.GlobalFrame - _lastLetterFrame - 1 >= _eouFrames)
                FinishOpen();
            else if (vocabulary.StartsWord(token.Id) && token.GlobalFrame - _firstLetterFrame >= _capFrames)
                FinishOpen();
        }

        _open.Add(token);
        if (_lexical[token.Id])
        {
            if (_openLetterTokens++ == 0)
                _firstLetterFrame = token.GlobalFrame;
            _lastLetterFrame = token.GlobalFrame;
        }

        if (vocabulary.IsTag(token.Id))
        {
            var tag = vocabulary.RawPiece(token.Id).Trim('<', '>');
            _lastTag = tag;
            _openLanguage = tag;
            if (_options.EndOnLanguageTag && _openLetterTokens > 0)
                FinishOpen();
        }
    }

    // Closes the open utterance when it holds letters (marks alone stay open and lead the next one, unless the stream ends here:
    // a flush commits them as a continuation, nothing is left open after it, as the interface promises).
    private void FinishOpen(bool endOfStream = false)
    {
        if (_openLetterTokens == 0 && !(endOfStream && HasMarks()))
            return;

        var language = ResolveLanguage(_openLanguage);
        var words = _timer.Build(_open, language, PaddingBefore, Mask(), _fedSamples, _lastWordEnd);
        _finished.Add(new FinishedUtterance(TextOf(_open, _options.KeepLanguageTagInFinal), words, ContinuesPreviousUtterance(_open)));
        _lastWordEnd = Math.Max(_lastWordEnd, words[^1].EndSeconds);

        _open.Clear();
        _openLetterTokens = 0;
        _firstLetterFrame = _lastLetterFrame = -1;
        _openLanguage = null;
    }

    // The open utterance holds a mark but no letter: the closing mark of the sentence before, written after the silence rule had
    // closed it.
    private bool HasMarks() => _open.Count > 0 && OpenText(keepTags: false).Length > 0;

    private StreamingResult TakeResult()
    {
        string? final = null;
        List<WordTiming>? finalWords = null;
        var continues = false;
        if (_finished.Count > 0)
        {
            // utterances that ended in this call: a continuation is attached to the text and to the last word of the one before
            var text = new StringBuilder(_finished[0].Text);
            finalWords = new List<WordTiming>(_finished[0].Words);
            foreach (var next in _finished.Skip(1))
            {
                if (next.ContinuesPrevious)
                {
                    text.Append(next.Text);
                    AttachWords(finalWords, next.Words);
                }
                else
                {
                    text.Append(' ').Append(next.Text);
                    finalWords.AddRange(next.Words);
                }
            }
            final = text.ToString();
            continues = _finished[0].ContinuesPrevious;
            _finished.Clear();
        }

        var partial = OpenText(keepTags: false);
        var partialWords = _open.Count == 0
            ? new List<WordTiming>()
            : _timer.Build(_open, ResolveLanguage(_openLanguage), PaddingBefore, Mask(), _fedSamples, _lastWordEnd);
        return new StreamingResult(partial, final)
        {
            Stable = partial,
            FinalWords = finalWords,
            PartialWords = partialWords,
            FinalContinuesPrevious = continues,
            PartialContinuesPrevious = _open.Count > 0 && ContinuesPreviousUtterance(_open),
        };
    }

    // The first word of a continuation belongs to the last word of the text before it ("there" + "s", "there" + "."). A mark
    // does not extend the word's speech, the rest of a word does.
    private static void AttachWords(List<WordTiming> words, IReadOnlyList<WordTiming> continuation)
    {
        var last = words[^1];
        var head = continuation[0];
        var isMark = !head.Word.Any(char.IsLetterOrDigit);
        words[^1] = last with
        {
            Word = last.Word + head.Word,
            EndSeconds = isMark ? last.EndSeconds : Math.Max(last.EndSeconds, head.EndSeconds),
            Tokens = last.Tokens is null ? head.Tokens : last.Tokens.Concat(head.Tokens ?? []).ToList(),
        };
        for (var i = 1; i < continuation.Count; i++)
            words.Add(continuation[i]);
    }

    // An utterance continues the previous one when its first piece carries no word marker: the closing mark of the previous
    // sentence, or the rest of a word whose pieces the model emitted more than the silence rule apart.
    private bool ContinuesPreviousUtterance(List<NemotronToken> tokens)
    {
        var vocabulary = _engine.Vocabulary;
        foreach (var token in tokens)
        {
            if (!vocabulary.IsTag(token.Id))
                return !vocabulary.StartsWord(token.Id);
        }
        return false;
    }

    private NemotronSpeechMask? Mask() =>
        _tracker is not null && _options.WordTiming.Snap is { } snap ? _tracker.Snapshot(snap) : null;

    // The language the lag of a word is chosen for: the utterance's own tag, else the language the engine was asked for,
    // else the last tag of the stream (language "auto" before the first tag is unknown).
    private string? ResolveLanguage(string? tag)
    {
        if (PrimaryLanguage(tag) is { } own)
            return own;
        if (_engine.LanguageId is 0 or 1)
            return "en";
        if (_engine.LanguageId == 11)
            return "ru";
        return _engine.LanguageId == 101 ? PrimaryLanguage(_lastTag) : null;
    }

    private static string? PrimaryLanguage(string? tag)
    {
        if (string.IsNullOrEmpty(tag))
            return null;
        var dash = tag.IndexOf('-');
        return (dash > 0 ? tag[..dash] : tag).ToLowerInvariant();
    }

    // ── text ────────────────────────────────────────────────────────────

    private string OpenText(bool keepTags) => TextOf(_open, keepTags);

    // The text of the tokens as the plain recognizer decodes it (tags optional), with one exception: a mark that the model
    // wrote as a word of its own ("hello ." = a bare word marker followed by "." ) is attached to the word before it, like
    // TimedWordBuilder does, so that the text has exactly one whitespace word per timed word.
    private string TextOf(IReadOnlyList<NemotronToken> tokens, bool keepTags)
    {
        if (tokens.Count == 0)
            return "";
        var vocabulary = _engine.Vocabulary;
        var text = new StringBuilder();
        var group = new StringBuilder();
        var core = new StringBuilder();
        var skipped = false;
        var wordSeen = false;
        for (var start = 0; start < tokens.Count;)
        {
            var end = start + 1;
            while (end < tokens.Count && !vocabulary.StartsWord(tokens[end].Id))
                end++;

            group.Clear();
            core.Clear();
            for (var k = start; k < end; k++)
            {
                var id = tokens[k].Id;
                if (vocabulary.IsTag(id))
                {
                    if (!keepTags)
                    {
                        skipped = true;
                        continue;
                    }
                }
                else
                {
                    core.Append(vocabulary.PieceOf(id));
                }
                vocabulary.AppendDecoded(group, id);
            }

            var word = core.ToString().Trim();
            var detachedMark = wordSeen && word.Length > 0 && !word.Any(char.IsLetterOrDigit);
            text.Append(detachedMark ? group.ToString().TrimStart() : group.ToString());
            wordSeen |= word.Length > 0;
            start = end;
        }

        var result = text.ToString();
        if (skipped)
            result = MultipleSpaces.Replace(result, " ");
        return result.Trim();
    }

    // ── lifetime ────────────────────────────────────────────────────────

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _offline?.Dispose();
        if (_ownsEngine)
            _engine.Dispose();
    }
}
