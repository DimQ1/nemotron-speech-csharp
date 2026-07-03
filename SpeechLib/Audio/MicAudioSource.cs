using NAudio.Wave;

namespace SpeechLib.Audio;

/// <summary>Microphone capture at 16 kHz mono with buffered ring-buffer drain.</summary>
public sealed class MicAudioSource : IAudioSource
{
    private const int DrainMs = 100;

    public int SourceSampleRate => 16000;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var device = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        var ring = new BufferedWaveProvider(device.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(30),
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
                var batch = DrainRing(ring);
                if (batch.Length > 0)
                {
                    buffer.Enqueue(batch);
                    signal.Set();
                }
            }
            catch { /* best-effort */ }
        }

        // Final drain — flush whatever is left
        try
        {
            var final = DrainRing(ring);
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

    /// <summary>Drain all buffered audio, apply gain, return as float[].</summary>
    private static float[] DrainRing(BufferedWaveProvider ring)
    {
        if (ring.BufferedDuration <= TimeSpan.Zero) return [];

        var sp = ring.ToSampleProvider();
        var frames = ring.BufferedBytes / (ring.WaveFormat.BitsPerSample / 8);
        if (frames <= 0) return [];

        var result = new float[frames];
        int read = sp.Read(result, 0, frames);
        if (read <= 0) return [];

        // Trim to actual read and apply gain
        var trimmed = read < result.Length ? result[..read] : result;
        for (int i = 0; i < trimmed.Length; i++)
            trimmed[i] *= 0.6f;

        return trimmed;
    }

    public void Dispose() { }
}
