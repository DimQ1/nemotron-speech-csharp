using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SpeechLib.Audio;

/// <summary>Microphone capture at 16kHz mono.</summary>
public sealed class MicAudioSource : IAudioSource
{
    public int SourceSampleRate => 16000;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var device = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        device.DataAvailable += (_, ev) =>
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
        device.RecordingStopped += (_, _) => { state.IsRunning = false; signal.Set(); };
        device.StartRecording();

        while (state.IsRunning) Thread.Sleep(100);
        device.StopRecording();
        device.Dispose();
    }

    public void Dispose() { }
}
