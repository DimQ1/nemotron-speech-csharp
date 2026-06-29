using NAudio.Wave;

namespace SpeechLib.Audio;

/// <summary>System audio loopback capture, resampled to target rate.</summary>
public sealed class LoopbackAudioSource : IAudioSource
{
    private readonly int _targetRate;

    public LoopbackAudioSource(int targetRate) => _targetRate = targetRate;
    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, ref bool isRunning)
    {
        bool running = isRunning;
        var device = new WasapiLoopbackCapture();
        var fmt = device.WaveFormat;
        device.DataAvailable += (_, ev) =>
        {
            if (!running) return;
            var samples = AudioUtils.Convert(ev.Buffer, ev.BytesRecorded, fmt);
            var downsampled = AudioUtils.Resample(samples, fmt.SampleRate, _targetRate, 0.5f);
            var batch = downsampled.ToArray();
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
