using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpeechLib.Models;
using System.Buffers;
using System.Runtime.Versioning;

namespace SpeechLib.Audio;

/// <summary>
/// Windows capture provider built against NAudio 3 preview.
/// The provider keeps the same batched float contract as the stable NAudio provider,
/// so switching providers does not change recognizer allocation behavior.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NAudio3AudioSource : IAudioSource
{
    private const int DrainIntervalMilliseconds = 100;
    private readonly CaptureMode _mode;
    private readonly int _targetRate;
    private CaptureState? _activeState;

    public static AudioLevelMeter AudioLevelMeter { get; } = new();

    /// <summary>Level meter for the microphone channel (pre-mix gain).</summary>
    public static AudioLevelMeter MicLevelMeter { get; } = new();

    /// <summary>Level meter for the loopback channel (pre-mix gain).</summary>
    public static AudioLevelMeter LoopbackLevelMeter { get; } = new();

    private static float _micVolume = 1.0f;
    private static float _loopbackVolume = 1.0f;

    /// <summary>
    /// Live toggle for adaptive echo cancellation (NLMS AEC) in Mix mode.
    /// When enabled, the loopback-derived echo estimate is subtracted from the
    /// microphone channel before mixing, removing speaker-to-mic echo.
    /// </summary>
    public static bool EchoCancellation { get; set; } = true;

    public static float MicVolume
    {
        get => _micVolume;
        set => _micVolume = Math.Clamp(value, 0f, 1f);
    }

    public static float LoopbackVolume
    {
        get => _loopbackVolume;
        set => _loopbackVolume = Math.Clamp(value, 0f, 1f);
    }

    public NAudio3AudioSource(CaptureMode mode, int targetRate)
    {
        if (mode is CaptureMode.File)
            throw new ArgumentException("A live capture mode is required.", nameof(mode));
        if (targetRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRate));

        _mode = mode;
        _targetRate = targetRate;
    }

    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(state);

        if (Interlocked.CompareExchange(ref _activeState, state, null) is not null)
            throw new InvalidOperationException("Audio capture is already running.");

        CaptureHandle? loopback = null;
        CaptureHandle? microphone = null;
        try
        {
            if (_mode is CaptureMode.Loopback or CaptureMode.Mix)
            {
                try
                {
                    loopback = CreateLoopback(state, signal);
                }
                catch (Exception ex)
                {
                    if (_mode == CaptureMode.Mix)
                    {
                        // Missing render device is not fatal in Mix mode — degrade to mic-only.
                        Console.Error.WriteLine($"[capture] Loopback unavailable — continuing with microphone only: {ex.Message}");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "No audio render device is available for system-audio (loopback) capture. " +
                            "Start playing audio, or run with a microphone instead.", ex);
                    }
                }
            }

            if (_mode is CaptureMode.Mic or CaptureMode.Mix)
            {
                try
                {
                    microphone = CreateMicrophone(state, signal);
                }
                catch (Exception ex)
                {
                    if (_mode == CaptureMode.Mix)
                    {
                        Console.Error.WriteLine($"[capture] Microphone unavailable — continuing with system audio only: {ex.Message}");
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            "The microphone could not be started. It may be in use by another application or disabled.", ex);
                    }
                }
            }

            if (loopback is null && microphone is null)
                throw new InvalidOperationException(
                    "No audio source could be started. Check your microphone and system-audio settings.");

            TryStart(loopback, "Loopback", signal, ref loopback);
            TryStart(microphone, "Microphone", signal, ref microphone);

            if (loopback is null && microphone is null)
                throw new InvalidOperationException(
                    "No audio source could be started. Check your microphone and system-audio settings.");

            var loopbackSource = loopback?.Buffer;
            var microphoneSource = microphone?.Buffer;
            var loopbackResampler = loopback?.Resampler;
            var microphoneResampler = microphone?.Resampler;
            // One AEC per capture session: filter taps converge over time and
            // must persist across drain cycles.
            var echoCanceller = loopback is not null && microphone is not null
                ? new NlmsEchoCanceller()
                : null;
            // One aligner per session: keeps mic/loopback time-aligned so the
            // AEC reference and the mix never drift apart (WASAPI clocks are
            // not synchronized between capture endpoints).
            var aligner = new ChannelAligner();

            try
            {
                while (state.IsRunning)
                {
                    state.Wait(DrainIntervalMilliseconds);
                    if (!state.IsRunning)
                        break;

                    DrainAndPublish(loopbackSource, microphoneSource, buffer, signal,
                        loopbackResampler, microphoneResampler, echoCanceller, aligner);
                }

                DrainAndPublish(loopbackSource, microphoneSource, buffer, signal,
                    loopbackResampler, microphoneResampler, echoCanceller, aligner);
            }
            finally
            {
                state.Stop();
                loopback?.StopRecording();
                microphone?.StopRecording();
            }
        }
        finally
        {
            loopback?.Dispose();
            microphone?.Dispose();
            Interlocked.CompareExchange(ref _activeState, null, state);
        }
    }

    /// <summary>
    /// Starts one capture handle, degrading Mix mode to the other source when the
    /// start call fails. Non-Mix failures are rethrown with an actionable message.
    /// </summary>
    private void TryStart(CaptureHandle? handle, string what, ManualResetEventSlim signal, ref CaptureHandle? target)
    {
        if (handle is null)
            return;

        try
        {
            handle.StartRecording();
        }
        catch (Exception ex)
        {
            if (_mode == CaptureMode.Mix)
            {
                Console.Error.WriteLine($"[capture] {what} failed to start — continuing with the other source: {ex.Message}");
                handle.Dispose();
                target = null;
            }
            else
            {
                throw new InvalidOperationException($"{what} capture could not start.", ex);
            }
        }
    }

    public void Dispose()
    {
        _activeState?.Stop();
    }

    private CaptureHandle? CreateLoopback(CaptureState state, ManualResetEventSlim signal) =>
        _mode is CaptureMode.Loopback or CaptureMode.Mix
            ? CaptureHandle.CreateLoopback(state, signal, _targetRate)
            : null;

    private CaptureHandle? CreateMicrophone(CaptureState state, ManualResetEventSlim signal) =>
        _mode is CaptureMode.Mic or CaptureMode.Mix
            ? CaptureHandle.CreateMicrophone(state, signal, _targetRate)
            : null;

    private static void DrainAndPublish(
        BufferedWaveProvider? loopback,
        BufferedWaveProvider? microphone,
        ConcurrentQueueWrapper buffer,
        ManualResetEventSlim signal,
        StreamingFirResampler? loopbackResampler = null,
        StreamingFirResampler? microphoneResampler = null,
        NlmsEchoCanceller? echoCanceller = null,
        ChannelAligner? aligner = null)
    {
        float[]? loopbackSamples = null;
        float[]? microphoneSamples = null;
        try
        {
            var loopbackCount = Drain(loopback, loopbackResampler, ref loopbackSamples);
            var microphoneCount = Drain(microphone, microphoneResampler, ref microphoneSamples);

            if (aligner is null)
                return;

            // Time-align both channels: the aligner emits equal-length windows,
            // carrying over excess samples of the faster clock to the next cycle.
            var count = aligner.Align(
                loopbackSamples.AsSpan(0, loopbackCount),
                microphoneSamples.AsSpan(0, microphoneCount),
                out var loopAligned,
                out var micAligned,
                out var loopActive,
                out var micActive);
            if (count == 0)
                return;

            // Adaptive echo cancellation: subtract the loopback-derived echo
            // estimate from the mic channel BEFORE metering/mixing, so both the
            // UI level and the recognizer see the echo-free microphone signal.
            if (echoCanceller is not null && EchoCancellation && loopActive && micActive)
            {
                echoCanceller.ProcessInPlace(
                    loopAligned.AsSpan(0, count),
                    micAligned.AsSpan(0, count));
            }

            // Per-channel levels (pre-mix gain) so the mixer UI can show each source
            if (micActive)
                MicLevelMeter.PublishIfActive(micAligned.AsSpan(0, count));
            if (loopActive)
                LoopbackLevelMeter.PublishIfActive(loopAligned.AsSpan(0, count));

            var batch = new float[count];
            for (var index = 0; index < count; index++)
            {
                // Normalize by the sum of active channel weights: no clipping when both
                // channels are loud, no attenuation when only one is present, and no
                // level step at the boundary where the shorter buffer ends.
                float weight = 0f;
                if (loopActive)
                    weight += LoopbackVolume;
                if (micActive)
                    weight += MicVolume;
                if (weight <= 0f)
                    continue;

                float s = 0f;
                if (loopActive)
                    s += loopAligned[index] * LoopbackVolume;
                if (micActive)
                    s += micAligned[index] * MicVolume;
                batch[index] = s / weight;
            }

            buffer.Enqueue(batch);
            AudioLevelMeter.Publish(batch);
            signal.Set();
        }
        finally
        {
            Return(loopbackSamples);
            Return(microphoneSamples);
        }
    }

    /// <summary>
    /// Drain a <see cref="BufferedWaveProvider"/> directly to mono float samples at the
    /// target rate. Steps: raw bytes → PCM/float decode → stereo average to mono →
    /// streaming FIR resample (windowed-sinc low-pass, state kept across drains).
    /// </summary>
    private static int Drain(BufferedWaveProvider? source, StreamingFirResampler? resampler, ref float[]? samples)
    {
        if (source is null)
            return 0;

        var fmt = source.WaveFormat;
        var bytes = source.BufferedBytes;
        var bytesPerFrame = fmt.BlockAlign;
        var frames = bytes / bytesPerFrame;
        if (frames <= 0)
            return 0;

        var raw = new byte[frames * bytesPerFrame];
        var read = source.Read(raw.AsSpan(0, raw.Length));
        frames = read / bytesPerFrame;
        if (frames <= 0)
            return 0;

        // Decode to mono float.
        var mono = ArrayPool<float>.Shared.Rent(frames);
        try
        {
            var channels = fmt.Channels;
            var bytesPerSample = fmt.BitsPerSample / 8;
            for (var f = 0; f < frames; f++)
            {
                var offset = f * bytesPerFrame;
                float sum = 0f;
                for (var c = 0; c < channels; c++)
                    sum += ReadSample(raw, offset + c * bytesPerSample, fmt);
                mono[f] = sum / channels;
            }

            if (resampler is null)
            {
                // Rates already match — pass mono through as-is.
                EnsureCapacity(ref samples, frames, 0);
                mono.AsSpan(0, frames).CopyTo(samples);
                return frames;
            }

            var maxOut = resampler.GetMaxOutputCount(frames);
            EnsureCapacity(ref samples, maxOut, 0);
            return resampler.Process(mono.AsSpan(0, frames), samples!);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(mono);
        }
    }

    /// <summary>Decode a single PCM16 or IEEE-float32 sample.</summary>
    private static float ReadSample(byte[] raw, int offset, WaveFormat fmt)
    {
        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat)
            return BitConverter.ToSingle(raw, offset);

        // 16-bit PCM (both mic WaveIn and typical capture formats)
        return BitConverter.ToInt16(raw, offset) / 32768f;
    }

    private static void EnsureCapacity(ref float[]? samples, int required, int count)
    {
        if (samples is not null && samples.Length >= required)
            return;

        var capacity = samples is null ? required : Math.Max(required, samples.Length * 2);
        var replacement = ArrayPool<float>.Shared.Rent(capacity);
        if (samples is not null)
        {
            samples.AsSpan(0, count).CopyTo(replacement);
            ArrayPool<float>.Shared.Return(samples);
        }

        samples = replacement;
    }

    private static void Return(float[]? samples)
    {
        if (samples is not null)
            ArrayPool<float>.Shared.Return(samples);
    }

    /// <summary>
    /// Streaming windowed-sinc FIR low-pass resampler for real-time speech capture.
    /// Processes audio chunk-by-chunk with a sliding window: the filter history
    /// (state) is carried across chunks, so there are no clicks at frame boundaries
    /// and no aliasing in the 4-8 kHz band that corrupts ASR features.
    /// Latency is the FIR group delay (~1 ms), independent of chunk size.
    /// </summary>
    public sealed class StreamingFirResampler
    {
        private const int Taps = 32; // 2 ms at 16 kHz — enough for ~50 dB stop-band
        private const int Phases = 64; // fractional-delay granularity
        private const int TotalTaps = Taps * Phases;

        private readonly float[] _h;      // windowed-sinc low-pass prototype
        private readonly float[] _state;  // sliding window of source samples
        private readonly double _step;    // source samples consumed per output sample
        private double _pos;              // current fractional source position

        private StreamingFirResampler(int sourceRate, int targetRate)
        {
            _step = (double)sourceRate / targetRate;
            _h = BuildWindowedSinc(sourceRate, targetRate);
            _state = new float[Taps];
            _pos = 0;
        }

        /// <summary>Returns a resampler, or null when the rates already match.</summary>
        public static StreamingFirResampler? Create(int sourceRate, int targetRate) =>
            sourceRate == targetRate ? null : new StreamingFirResampler(sourceRate, targetRate);

        public int GetMaxOutputCount(int inputFrames) =>
            (int)((inputFrames + _pos) / _step) + 2;

        /// <summary>
        /// Resample one chunk of mono source samples into <paramref name="dst"/>.
        /// Filter state and the fractional position persist across calls.
        /// </summary>
        public int Process(ReadOnlySpan<float> src, float[] dst)
        {
            var outCount = 0;
            var total = _state.Length + src.Length; // virtual buffer: history + new data

            while (true)
            {
                var i0 = (int)Math.Floor(_pos);
                var frac = _pos - i0;
                // The newest sample of the window must lie inside the virtual buffer.
                if (i0 + Taps - 1 >= total)
                    break;

                var phase = (int)(frac * Phases);
                if (phase >= Phases) phase = Phases - 1;

                float acc = 0f;
                var hBase = phase;
                for (var k = 0; k < Taps; k++)
                {
                    var vIdx = i0 + k;
                    float v = vIdx < _state.Length
                        ? _state[vIdx]
                        : src[vIdx - _state.Length];
                    acc += v * _h[hBase + k * Phases];
                }
                dst[outCount++] = acc;
                _pos += _step;
            }

            // Shift the sliding window: keep the last Taps source samples as history.
            // consumed is the number of virtual-buffer samples we can discard; since
            // the state length is Taps, consumed == src.Length always holds, so the
            // new history is simply the tail of (old history + new chunk).
            var consumed = src.Length;
            if (consumed >= Taps)
            {
                src.Slice(consumed - Taps).CopyTo(_state);
            }
            else
            {
                Array.Copy(_state, consumed, _state, 0, Taps - consumed);
                src.CopyTo(_state.AsSpan(Taps - consumed));
            }
            _pos -= consumed;

            return outCount;
        }

        private static float[] BuildWindowedSinc(int sourceRate, int targetRate)
        {
            // Cutoff just below min(Nyquist) of the two rates; Blackman window.
            var nyquist = Math.Min(sourceRate, targetRate) * 0.5;
            var fc = nyquist * 0.9 / sourceRate; // normalized to source rate
            var h = new float[TotalTaps];
            var mid = (Taps - 1) * 0.5;

            for (var k = 0; k < Taps; k++)
            {
                for (var p = 0; p < Phases; p++)
                {
                    var n = k + (double)p / Phases - mid;
                    double sinc = n == 0
                        ? 2 * Math.PI * fc
                        : Math.Sin(2 * Math.PI * fc * n) / n;
                    var w = 0.42 - 0.5 * Math.Cos(2 * Math.PI * (k * Phases + p) / (TotalTaps - 1))
                                  + 0.08 * Math.Cos(4 * Math.PI * (k * Phases + p) / (TotalTaps - 1));
                    h[k * Phases + p] = (float)(sinc * w);
                }
            }

            // Normalize the DC gain of the integer phase to 1.
            double phase0Sum = 0;
            for (var k = 0; k < Taps; k++) phase0Sum += h[k * Phases];
            var scale = (float)(1.0 / phase0Sum);
            for (var i = 0; i < h.Length; i++) h[i] *= scale;
            return h;
        }
    }

// CS0618: WasapiCapture/WasapiLoopbackCapture are deprecated in NAudio 3 preview in
// favour of WasapiRecorderBuilder, but the builder API never raised DataAvailable for
// loopback in this scenario (capture-diag.log investigation), so the proven classes stay.
#pragma warning disable CS0618
    private sealed class CaptureHandle : IDisposable
    {
        private readonly WasapiCapture? _microphone;
        private readonly WasapiLoopbackCapture? _loopback;

        private CaptureHandle(WasapiCapture microphone, int targetRate)
        {
            _microphone = microphone;
            Buffer = CreateBuffer(microphone.WaveFormat);
            Resampler = StreamingFirResampler.Create(microphone.WaveFormat.SampleRate, targetRate);
        }

        private CaptureHandle(WasapiLoopbackCapture loopback, int targetRate)
        {
            _loopback = loopback;
            Buffer = CreateBuffer(loopback.WaveFormat);
            Resampler = StreamingFirResampler.Create(loopback.WaveFormat.SampleRate, targetRate);
        }

        public BufferedWaveProvider Buffer { get; }

        /// <summary>Per-channel streaming FIR resampler; null when no rate conversion is needed.</summary>
        public StreamingFirResampler? Resampler { get; }

        private static BufferedWaveProvider CreateBuffer(WaveFormat format) =>
            new(format, TimeSpan.FromSeconds(2))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

        public static CaptureHandle CreateMicrophone(CaptureState state, ManualResetEventSlim signal, int targetRate)
        {
            // WasapiCapture (shared mode) replaces WaveIn/WinMM so the whole
            // capture path stays portable (NAudio.Wasapi targets net9.0). It
            // records the default capture endpoint; the Drain step resamples to
            // 16 kHz mono regardless of the device mix format.
            var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            var capture = new WasapiCapture(device);
            var handle = new CaptureHandle(capture, targetRate);
            handle._microphone!.DataAvailable += (_, args) =>
            {
                if (state.IsRunning)
                    handle.Buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
            };
            handle._microphone.RecordingStopped += (_, _) =>
            {
                state.Stop();
                signal.Set();
            };
            return handle;
        }

        public static CaptureHandle CreateLoopback(CaptureState state, ManualResetEventSlim signal, int targetRate)
        {
            // WasapiLoopbackCapture is the proven loopback path (same as the NAudio2 provider):
            // the WasapiRecorder builder API in the NAudio 3 preview never raised DataAvailable
            // in this scenario, so loopback stayed silent (see capture-diag.log investigation).
            var capture = new WasapiLoopbackCapture();
            var handle = new CaptureHandle(capture, targetRate);
            handle._loopback!.DataAvailable += (_, args) =>
            {
                if (state.IsRunning)
                    handle.Buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
            };
            handle._loopback.RecordingStopped += (_, _) =>
            {
                state.Stop();
                signal.Set();
            };
            return handle;
        }

        public void StartRecording()
        {
            _microphone?.StartRecording();
            _loopback?.StartRecording();
        }

        public void StopRecording()
        {
            _microphone?.StopRecording();
            _loopback?.StopRecording();
        }

        public void Dispose()
        {
            _microphone?.Dispose();
            _loopback?.Dispose();
        }
    }
#pragma warning restore CS0618
}
