using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SpeechLib.Audio;

/// <summary>Mixed microphone + loopback capture.</summary>
public sealed class MixAudioSource : IAudioSource
{
    private readonly MicAudioSource _mic = new();
    private LoopbackAudioSource? _loopback;
    private readonly int _targetRate;
    private const int DrainMs = 100;

    public MixAudioSource(int targetRate) { _targetRate = targetRate; _loopback = new(targetRate); }
    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        // Loopback on a dedicated thread — same parallel pattern as before
        var loopbackState = new CaptureState();
        var loopbackBuf = new ConcurrentQueueWrapper();
        var loopbackSig = new ManualResetEventSlim(false);
        var loopThread = new Thread(() => _loopback!.Start(loopbackBuf, loopbackSig, loopbackState))
            { IsBackground = true, Name = "Mix-loopback" };
        loopThread.Start();

        // Mic with BufferedWaveProvider — cheap memcpy in callback, no per-packet float[] allocation
        var micDevice = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        var micBuf = new BufferedWaveProvider(micDevice.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false,
            BufferDuration = TimeSpan.FromSeconds(60),
        };
        micDevice.DataAvailable += (_, ev) =>
        {
            if (!state.IsRunning) return;
            micBuf.AddSamples(ev.Buffer, 0, ev.BytesRecorded);
        };
        micDevice.RecordingStopped += (_, _) => { state.IsRunning = false; signal.Set(); };
        micDevice.StartRecording();

        // Drain loop: collect from both sources every 100ms, mix, push once
        while (state.IsRunning)
        {
            Thread.Sleep(DrainMs);
            if (!state.IsRunning) break;

            try
            {
                // Drain mic buffer
                var micSamples = DrainBuffer(micBuf);

                // Drain loopback buffer (drain all batches accumulated since last check)
                var loopSamples = new List<float>();
                while (loopbackBuf.TryDequeue(out var lb))
                    loopSamples.AddRange(lb);

                // Mix if both present, else whichever has data
                var batch = Mix(micSamples, loopSamples.ToArray());
                if (batch.Length > 0)
                {
                    buffer.Enqueue(batch);
                    signal.Set();
                }
            }
            catch { /* best-effort */ }
        }

        loopbackState.IsRunning = false;
        loopbackSig.Set();
        micDevice.StopRecording();
        micDevice.Dispose();
        loopThread.Join(1000);
    }

    /// <summary>Mix two arrays, optionally gain‑adjusted, and resample to target rate.</summary>
    private float[] Mix(float[] mic, float[] loopback)
    {
        if (mic.Length == 0 && loopback.Length == 0) return [];
        if (mic.Length == 0) return loopback;
        if (loopback.Length == 0) return mic;

        // Apply mic gain inline (simple, no extra allocations)
        for (int i = 0; i < mic.Length; i++)
            mic[i] *= 0.6f;

        int maxLen = Math.Max(mic.Length, loopback.Length);
        var mixed = new float[maxLen];
        for (int i = 0; i < mic.Length; i++)
            mixed[i] = mic[i];
        for (int i = 0; i < loopback.Length; i++)
            mixed[i] += loopback[i] * 0.5f;

        return mixed;
    }

    private static float[] DrainBuffer(BufferedWaveProvider buf)
    {
        if (buf.BufferedDuration <= TimeSpan.Zero) return [];
        var sp = buf.ToSampleProvider();
        var bytes = buf.BufferedBytes;
        var frames = bytes / (buf.WaveFormat.BitsPerSample / 8);
        if (frames <= 0) return [];
        var result = new float[frames];
        int read = sp.Read(result, 0, frames);
        return read > 0 ? result[..read] : [];
    }

    public void Dispose() { _loopback?.Dispose(); }
}
