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
            micCapture = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
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

        // Build lazy normalization pipeline(s) — mono + resample direct from ring buffers.
        // No pre-drain, no intermediate float[] copies.
        ISampleProvider? loopbackSource = loopbackProvider is not null
            ? CreateNormalizedProvider(loopbackProvider) : null;
        ISampleProvider? micSource = micProvider is not null
            ? CreateNormalizedProvider(micProvider) : null;

        if (loopbackSource is null && micSource is null)
            throw new InvalidOperationException("No audio sources configured");

        // ── Main drain loop — drain all available audio each cycle ──
        // Reads ALL buffered audio from each source independently and
        // mixes them. A silent source is zero-filled so the other continues
        // uninterrupted (unlike MixingSampleProvider which blocks on empty inputs).
        var chunkBuf = new float[4096];
        while (state.IsRunning)
        {
            Thread.Sleep(DrainIntervalMs);
            if (!state.IsRunning) break;

            try
            {
                var loopSamples = DrainAll(loopbackSource, chunkBuf);
                var micSamples = DrainAll(micSource, chunkBuf);

                int count = Math.Max(loopSamples.Count, micSamples.Count);
                if (count == 0) continue;

                var batch = new float[count];
                for (int i = 0; i < count; i++)
                {
                    float s = 0f;
                    if (i < loopSamples.Count) s += loopSamples[i];
                    if (i < micSamples.Count) s += micSamples[i];
                    batch[i] = s;
                }

                buffer.Enqueue(batch);
                signal.Set();
            }
            catch
            {
                // Swallow drain errors — best-effort capture
            }
        }

        // ── Final drain ────────────────────────────────────────────
        try
        {
            var loopSamples = DrainAll(loopbackSource, chunkBuf);
            var micSamples = DrainAll(micSource, chunkBuf);

            int count = Math.Max(loopSamples.Count, micSamples.Count);
            if (count > 0)
            {
                var batch = new float[count];
                for (int i = 0; i < count; i++)
                {
                    float s = 0f;
                    if (i < loopSamples.Count) s += loopSamples[i];
                    if (i < micSamples.Count) s += micSamples[i];
                    batch[i] = s;
                }
                buffer.Enqueue(batch);
                signal.Set();
            }
        }
        catch { }

        // ── Cleanup ────────────────────────────────────────────────
        loopbackCapture?.StopRecording();
        loopbackCapture?.Dispose();
        micCapture?.StopRecording();
        micCapture?.Dispose();

        // Dispose the normalization pipeline.
        // WdlResamplingSampleProvider holds unmanaged WDL resampler state.
        (loopbackSource as IDisposable)?.Dispose();
        (micSource as IDisposable)?.Dispose();
    }

    /// <summary>
    /// Wraps a <see cref="BufferedWaveProvider"/> in a lazy normalization pipeline:
    /// mono downmix + resample to target rate. Reads flow directly from the ring
    /// buffer — no intermediate float[] allocations.
    /// </summary>
    private ISampleProvider CreateNormalizedProvider(BufferedWaveProvider provider)
    {
        ISampleProvider source = provider.ToSampleProvider();

        if (provider.WaveFormat.Channels > 1)
            source = source.ToMono();

        if (provider.WaveFormat.SampleRate != _targetRate)
            source = new WdlResamplingSampleProvider(source, _targetRate);

        return source;
    }

    /// <summary>Read all available samples from a source into a list.</summary>
    private static List<float> DrainAll(ISampleProvider? source, float[] readBuf)
    {
        var result = new List<float>();
        if (source is null) return result;

        int read;
        while ((read = source.Read(readBuf, 0, readBuf.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                result.Add(readBuf[i]);
        }
        return result;
    }

    private static BufferedWaveProvider CreateBuffer(WaveFormat format) => new(format)
    {
        DiscardOnBufferOverflow = true,
        ReadFully = false,
        BufferDuration = TimeSpan.FromSeconds(60),
    };

    public void Dispose() { }
}
