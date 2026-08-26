namespace SpeechLib.Audio;

/// <summary>
/// Universal Decorator that adds voice-activity gating to ANY
/// <see cref="IStreamingSpeechRecognizer"/> (Parakeet today, any future model).
/// When VAD is enabled, audio windows scored as silence are dropped before
/// reaching the inner recognizer — saving encoder CPU during pauses. A short
/// pre-speech buffer and trailing hangover keep word edges intact.
///
/// Speech forwarding is transparent: while speech is active the inner
/// recognizer receives the exact same stream it would without the wrapper.
/// </summary>
public sealed class VadSpeechRecognizer : IStreamingSpeechRecognizer, IRuntimeConfigurable
{
    private readonly IStreamingSpeechRecognizer _inner;
    private readonly IVadFilter _vad;
    private readonly List<float> _preSpeech = new();
    private readonly int _preSpeechSamples;
    private readonly int _hangoverWindows;
    private bool _vadEnabled = true;
    private bool _inSpeech;
    private int _silenceWindows;
    private bool _disposed;

    /// <param name="inner">The recognizer being gated.</param>
    /// <param name="vad">The voice-activity detector (owned by this wrapper).</param>
    /// <param name="preSpeechMs">Audio kept before speech onset to preserve the first phoneme.</param>
    /// <param name="hangoverMs">Silence tolerated after speech before gating again (keeps trailing words).</param>
    public VadSpeechRecognizer(
        IStreamingSpeechRecognizer inner,
        IVadFilter vad,
        int preSpeechMs = 250,
        int hangoverMs = 600)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _vad = vad ?? throw new ArgumentNullException(nameof(vad));
        _preSpeechSamples = inner.SampleRate * preSpeechMs / 1000;
        _hangoverWindows = Math.Max(1, hangoverMs / 32); // 32 ms per VAD window
    }

    /// <inheritdoc />
    public int SampleRate => _inner.SampleRate;

    /// <inheritdoc />
    public int ChunkSamples => _inner.ChunkSamples;

    /// <inheritdoc />
    public int LastTokenCount => _inner.LastTokenCount;

    /// <summary>True when VAD gating is active (false = passthrough).</summary>
    public bool VadEnabled => _vadEnabled;

    /// <inheritdoc />
    public string? ProcessAudio(float[] chunk)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_vadEnabled)
            return _inner.ProcessAudio(chunk);

        if (_vad.HasSpeech(chunk))
        {
            _silenceWindows = 0;

            if (!_inSpeech)
            {
                // Speech onset: flush the pre-speech ring so the first word is intact.
                _inSpeech = true;
                if (_preSpeech.Count > 0)
                    _inner.ProcessAudio(_preSpeech.ToArray());
            }
            return _inner.ProcessAudio(chunk);
        }

        // Silence in this batch.
        if (_inSpeech)
        {
            int windows = Math.Max(1, chunk.Length / SileroVadFilter.HopSamples);
            _silenceWindows += windows;
            if (_silenceWindows < _hangoverWindows)
                return _inner.ProcessAudio(chunk); // hangover: keep trailing words

            // Long enough silence: close the utterance. Flush the inner
            // recognizer so the buffered tail (last words) is emitted now,
            // then reset its streaming state for a clean next utterance.
            _inSpeech = false;
            _silenceWindows = 0;
            _preSpeech.Clear();
            _vad.Reset();
            var tail = _inner.Flush();
            _inner.ResetStreamingState();
            return tail;
        }

        // Between utterances: keep a short ring so the next onset has context.
        AppendRing(_preSpeech, chunk, _preSpeechSamples);
        return null;
    }

    /// <inheritdoc />
    public string? Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Passthrough: forward the flush so the tail is never lost.
        if (!_vadEnabled)
            return _inner.Flush();

        // Gating: only forward if we were mid-utterance (audio reached the inner).
        return _inSpeech ? _inner.Flush() : null;
    }

    /// <inheritdoc />
    public void ResetStreamingState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inSpeech = false;
        _silenceWindows = 0;
        _preSpeech.Clear();
        _inner.ResetStreamingState();
    }

    // ---- IRuntimeConfigurable ----

    /// <inheritdoc />
    public bool TrySetVad(bool enabled)
    {
        if (_vadEnabled == enabled) return true;
        _vadEnabled = enabled;

        // Re-entering passthrough or gating: reset streaming state for a clean cut.
        _inSpeech = false;
        _silenceWindows = 0;
        _preSpeech.Clear();
        _vad.Reset();
        return true;
    }

    /// <inheritdoc />
    public bool TrySetSearchOptions(int numBeams, double repetitionPenalty) =>
        (_inner as IRuntimeConfigurable)?.TrySetSearchOptions(numBeams, repetitionPenalty) == true;

    private static void AppendRing(List<float> ring, float[] chunk, int capacity)
    {
        if (capacity <= 0) return;
        ring.AddRange(chunk);
        int excess = ring.Count - capacity;
        if (excess > 0)
            ring.RemoveRange(0, excess);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _vad.Dispose();
        // Do NOT dispose _inner: ownership stays with the caller (RecognitionService).
    }
}
