using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Runtime.InteropServices;

namespace SpeechLib.Audio;

/// <summary>System audio loopback capture, resampled to target rate via buffered ring-buffer drain.</summary>
public sealed class LoopbackAudioSource : IAudioSource
{
    private readonly int _targetRate;
    private const int DrainMs = 100;

    public LoopbackAudioSource(int targetRate) => _targetRate = targetRate;
    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var device = new WasapiLoopbackCapture();
        var fmt = device.WaveFormat;
        var ring = new BufferedWaveProvider(fmt)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(60),
        };

        device.DataAvailable += (_, ev) =>
        {
            if (!state.IsRunning) return;
            ring.AddSamples(ev.Buffer, 0, ev.BytesRecorded);
        };
        device.StartRecording();

        while (state.IsRunning)
        {
            Thread.Sleep(DrainMs);
            if (!state.IsRunning) break;

            try
            {
                var batch = DrainRing(ring, fmt);
                if (batch.Length > 0)
                {
                    buffer.Enqueue(batch);
                    signal.Set();
                }
            }
            catch { /* best-effort */ }
        }

        // Final drain
        try
        {
            var final = DrainRing(ring, fmt);
            if (final.Length > 0)
            {
                buffer.Enqueue(final);
                signal.Set();
            }
        }
        catch { }

        device.StopRecording();
        device.Dispose();
    }

    /// <summary>Drain ring buffer → mono → resample → gain → float[].</summary>
    private float[] DrainRing(BufferedWaveProvider ring, WaveFormat srcFmt)
    {
        if (ring.BufferedDuration <= TimeSpan.Zero) return [];

        var bytes = ring.BufferedBytes;
        var frames = bytes / (srcFmt.BitsPerSample / 8);
        if (frames <= 0) return [];

        var raw = new float[frames];
        int read = ring.ToSampleProvider().Read(raw, 0, frames);
        if (read <= 0) return [];

        var trimmed = read < raw.Length ? raw[..read] : raw;

        // Mono downmix (loopback is usually stereo)
        if (srcFmt.Channels > 1)
        {
            int monoLen = trimmed.Length / srcFmt.Channels;
            var mono = new float[monoLen];
            for (int i = 0; i < monoLen; i++)
            {
                float sum = 0;
                for (int ch = 0; ch < srcFmt.Channels; ch++)
                    sum += trimmed[i * srcFmt.Channels + ch];
                mono[i] = sum / srcFmt.Channels;
            }
            trimmed = mono;
        }

        // Resample to target rate via WDL (high quality)
        if (srcFmt.SampleRate != _targetRate)
        {
            var wdl = new WdlResamplingSampleProvider(
                new FloatArraySampleProvider(trimmed, new WaveFormat(srcFmt.SampleRate, 1)),
                _targetRate);
            var resampled = new List<float>(_targetRate * 2);
            var buf = new float[4096];
            int n;
            while ((n = wdl.Read(buf, 0, buf.Length)) > 0)
            {
                for (int i = 0; i < n; i++)
                    resampled.Add(buf[i]);
            }
            trimmed = resampled.ToArray();
        }

        // Gain
        for (int i = 0; i < trimmed.Length; i++)
            trimmed[i] *= 0.5f;

        return trimmed;
    }

    /// <summary>Wraps a float[] as an <see cref="ISampleProvider"/>.</summary>
    private sealed class FloatArraySampleProvider(float[] samples, WaveFormat fmt) : ISampleProvider
    {
        private int _pos;
        public WaveFormat WaveFormat { get; } = fmt;

        public int Read(float[] buffer, int offset, int count)
        {
            int avail = samples.Length - _pos;
            int copy = Math.Min(count, avail);
            if (copy <= 0) return 0;
            Array.Copy(samples, _pos, buffer, offset, copy);
            _pos += copy;
            return copy;
        }
    }

    public void Dispose() { }
}
