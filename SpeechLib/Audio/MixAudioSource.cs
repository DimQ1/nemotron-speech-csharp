using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SpeechLib.Audio;

/// <summary>Mixed microphone + loopback capture.</summary>
public sealed class MixAudioSource : IAudioSource
{
    private readonly MicAudioSource _mic = new();
    private LoopbackAudioSource? _loopback;
    private readonly int _targetRate;

    public MixAudioSource(int targetRate) { _targetRate = targetRate; _loopback = new(targetRate); }
    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var loopbackState = new CaptureState();
        var loopThread = new Thread(() => _loopback!.Start(buffer, signal, loopbackState))
            { IsBackground = true };
        loopThread.Start();

        var micDevice = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        micDevice.DataAvailable += (_, ev) =>
        {
            if (!state.IsRunning) return;
            int n = ev.BytesRecorded / 2;
            var batch = new float[n];
            var pcm = MemoryMarshal.Cast<byte, short>(ev.Buffer.AsSpan(0, ev.BytesRecorded));
            for (int i = 0; i < n; i++)
                batch[i] = pcm[i] / 32768f * 0.6f;
            buffer.Enqueue(batch);
            signal.Set();
        };
        micDevice.RecordingStopped += (_, _) => { state.IsRunning = false; signal.Set(); };
        micDevice.StartRecording();

        while (state.IsRunning) Thread.Sleep(100);
        loopbackState.IsRunning = false;
        micDevice.StopRecording();
        micDevice.Dispose();
        loopThread.Join(2000);
    }

    public void Dispose() { _loopback?.Dispose(); }
}
