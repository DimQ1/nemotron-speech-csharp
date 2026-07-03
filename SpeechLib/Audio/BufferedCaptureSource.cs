using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SpeechLib.Models;

namespace SpeechLib.Audio;

/// <summary>
/// Buffered audio capture using NAudio's <see cref="BufferedWaveProvider"/> ring buffers.
/// Inspired by the Whisper-HybridLoop-Onnx-Demo approach:
/// - Uses <see cref="WasapiLoopbackCapture"/> and/or <see cref="WaveInEvent"/>
/// - Feeds into <see cref="BufferedWaveProvider"/> per source
/// - Periodically drains, mixes (if both sources), and resamples to target rate
/// - Pushes final float[] mono 16kHz batches into the shared queue
/// </summary>
public sealed class BufferedCaptureSource : IAudioSource
{
    private readonly CaptureMode _mode;
    private readonly int _targetRate;
    private const int DrainIntervalMs = 100; // drain every 100 ms, matching demo pattern

    public BufferedCaptureSource(CaptureMode mode, int targetRate)
    {
        _mode = mode;
        _targetRate = targetRate;
    }

    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        // ── Set up capture devices ──────────────────────────────────
        var useLoopback = _mode is CaptureMode.Loopback or CaptureMode.Mix;
        var useMic = _mode is CaptureMode.Mic or CaptureMode.Mix;

        WasapiLoopbackCapture? loopbackCapture = null;
        WaveInEvent? micCapture = null;

        BufferedWaveProvider? loopbackProvider = null;
        BufferedWaveProvider? micProvider = null;

        if (useLoopback)
        {
            loopbackCapture = new WasapiLoopbackCapture();
            loopbackProvider = CreateBuffer(loopbackCapture.WaveFormat);
            loopbackCapture.DataAvailable += (_, ev) =>
            {
                if (!state.IsRunning) return;
                loopbackProvider.AddSamples(ev.Buffer, 0, ev.BytesRecorded);
            };
            loopbackCapture.RecordingStopped += (_, _) =>
            {
                state.IsRunning = false;
                signal.Set();
            };
            loopbackCapture.StartRecording();
        }

        if (useMic)
        {
            micCapture = new WaveInEvent();
            // Use 16 kHz mono for mic — same as current MicAudioSource
            micCapture.WaveFormat = new WaveFormat(16000, 16, 1);
            micProvider = CreateBuffer(micCapture.WaveFormat);
            micCapture.DataAvailable += (_, ev) =>
            {
                if (!state.IsRunning) return;
                micProvider.AddSamples(ev.Buffer, 0, ev.BytesRecorded);
            };
            micCapture.RecordingStopped += (_, _) =>
            {
                state.IsRunning = false;
                signal.Set();
            };
            micCapture.StartRecording();
        }

        // ── Main drain loop (timer-like, runs on this thread) ──────
        while (state.IsRunning)
        {
            Thread.Sleep(DrainIntervalMs);
            if (!state.IsRunning) break;

            try
            {
                var batch = DrainBuffers(loopbackProvider, micProvider);
                if (batch.Length > 0)
                {
                    buffer.Enqueue(batch);
                    signal.Set();
                }
            }
            catch
            {
                // Swallow drain errors — best-effort capture
            }
        }

        // ── Final drain ─────────────────────────────────────────────
        try
        {
            var finalBatch = DrainBuffers(loopbackProvider, micProvider);
            if (finalBatch.Length > 0)
            {
                buffer.Enqueue(finalBatch);
                signal.Set();
            }
        }
        catch { }

        // ── Cleanup ─────────────────────────────────────────────────
        loopbackCapture?.StopRecording();
        loopbackCapture?.Dispose();
        micCapture?.StopRecording();
        micCapture?.Dispose();
    }

    /// <summary>
    /// Drain all available audio from the buffer(s), mix if both are present,
    /// resample to target rate, and return as mono float[].
    /// </summary>
    private float[] DrainBuffers(BufferedWaveProvider? loopback, BufferedWaveProvider? mic)
    {
        var sources = new List<ISampleProvider>(2);

        if (loopback is not null && loopback.BufferedDuration > TimeSpan.Zero)
        {
            var loopbackSamples = ReadAllSamples(loopback);
            if (loopbackSamples.Length > 0)
                sources.Add(CreateNormalizedSource(loopbackSamples, loopback.WaveFormat, 0.5f));
        }

        if (mic is not null && mic.BufferedDuration > TimeSpan.Zero)
        {
            var micSamples = ReadAllSamples(mic);
            if (micSamples.Length > 0)
                sources.Add(CreateNormalizedSource(micSamples, mic.WaveFormat, 0.6f));
        }

        if (sources.Count == 0)
            return [];

        // Mix multiple sources or pass single source through
        ISampleProvider mixed = sources.Count switch
        {
            1 => sources[0],
            _ => new MixingSampleProvider(sources),
        };

        // Read all available samples
        var result = new List<float>(_targetRate * 2); // ~2 second capacity
        var readBuf = new float[4096];
        int read;
        while ((read = mixed.Read(readBuf, 0, readBuf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                result.Add(readBuf[i]);
        }

        return result.ToArray();
    }

    /// <summary>Read all currently buffered samples from a BufferedWaveProvider.</summary>
    private static float[] ReadAllSamples(BufferedWaveProvider provider)
    {
        var sampleProvider = provider.ToSampleProvider();
        var bufferedBytes = provider.BufferedBytes;
        var maxFrames = bufferedBytes / (provider.WaveFormat.BitsPerSample / 8);
        if (maxFrames <= 0) return [];

        var buffer = new float[maxFrames];
        int totalRead = sampleProvider.Read(buffer, 0, maxFrames);
        if (totalRead <= 0) return [];

        // Trim to actual read
        if (totalRead < buffer.Length)
            Array.Resize(ref buffer, totalRead);

        return buffer;
    }

    private ISampleProvider CreateNormalizedSource(float[] samples, WaveFormat sourceFormat, float gain)
    {
        ISampleProvider source = new FloatArraySampleProvider(samples, sourceFormat);

        if (source.WaveFormat.Channels > 1)
            source = source.ToMono();

        if (source.WaveFormat.SampleRate != _targetRate)
            source = new WdlResamplingSampleProvider(source, _targetRate);

        if (Math.Abs(gain - 1f) > float.Epsilon)
            source = new VolumeSampleProvider(source) { Volume = gain };

        return source;
    }

    private static BufferedWaveProvider CreateBuffer(WaveFormat format) => new(format)
    {
        DiscardOnBufferOverflow = true,
        ReadFully = false,
        BufferDuration = TimeSpan.FromSeconds(60),
    };

    public void Dispose() { }

    /// <summary>
    /// Wraps a float[] array as an <see cref="ISampleProvider"/> with the given source format.
    /// Used to feed drained buffers into the mixing pipeline.
    /// </summary>
    private sealed class FloatArraySampleProvider(float[] samples, WaveFormat sourceFormat) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat { get; } = sourceFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int available = samples.Length - _position;
            int toCopy = Math.Min(count, available);
            if (toCopy <= 0) return 0;

            Array.Copy(samples, _position, buffer, offset, toCopy);
            _position += toCopy;
            return toCopy;
        }
    }
}
