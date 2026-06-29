using NAudio.Wave;

namespace SpeechLib.Audio;

/// <summary>Microphone capture at 16kHz mono.</summary>
public sealed class MicAudioSource : IAudioSource
{
    public int SourceSampleRate => 16000;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, ref bool isRunning)
    {
        bool running = isRunning;
        var device = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        device.DataAvailable += (_, ev) =>
        {
            if (!running) return;
            int n = ev.BytesRecorded / 2;
            var batch = new float[n];
            for (int i = 0; i < n; i++)
                batch[i] = BitConverter.ToInt16(ev.Buffer, i * 2) / 32768f * 0.6f;
            buffer.Enqueue(batch);
            signal.Set();
        };
        device.RecordingStopped += (_, _) => { running = false; signal.Set(); };
        device.StartRecording();

        while (running) Thread.Sleep(100);
        isRunning = running;
        device.StopRecording();
        device.Dispose();
    }

    public void Dispose() { }
}
