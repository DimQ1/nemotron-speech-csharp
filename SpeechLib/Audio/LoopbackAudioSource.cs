using NAudio.Wave;

namespace SpeechLib.Audio;

/// <summary>System audio loopback capture, resampled to target rate.</summary>
public sealed class LoopbackAudioSource : IAudioSource
{
    private readonly int _targetRate;

    public LoopbackAudioSource(int targetRate) => _targetRate = targetRate;
    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        var device = new WasapiLoopbackCapture();
        var fmt = device.WaveFormat;
        device.DataAvailable += (_, ev) =>
        {
            if (!state.IsRunning) return;
            var samples = AudioUtils.Convert(ev.Buffer, ev.BytesRecorded, fmt);
            var batch = AudioUtils.Resample(samples, fmt.SampleRate, _targetRate, 0.5f);
            if (batch.Length > 0)
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
