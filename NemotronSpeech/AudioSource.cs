using NAudio.Wave;
using System.Collections.Concurrent;

namespace NemotronSpeech;

/// <summary>Audio source abstraction for live capture.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Sample rate of the captured audio (before resampling).</summary>
    int SourceSampleRate { get; }

    /// <summary>
    /// Start capturing. Samples are pushed as <c>float[]</c> batches to <paramref name="buffer"/>
    /// (lock-free queue) and <paramref name="signal"/> is set whenever new data arrives.
    /// </summary>
    void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, ref bool isRunning);
}

/// <summary>
/// Thin wrapper around ConcurrentQueue that accepts float[] batches.
/// Avoids per-sample atomic operations (was ~16000/sec, now ~10/sec).
/// </summary>
public sealed class ConcurrentQueueWrapper
{
    private readonly ConcurrentQueue<float[]> _queue = new();
    public void Enqueue(float[] batch) => _queue.Enqueue(batch);
    public bool TryDequeue(out float[] batch) => _queue.TryDequeue(out batch!);
    public bool IsEmpty => _queue.IsEmpty;
}

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

/// <summary>Mixed microphone + loopback capture.</summary>
public sealed class MixAudioSource : IAudioSource
{
    private readonly MicAudioSource _mic = new();
    private LoopbackAudioSource? _loopback;
    private readonly int _targetRate;

    public MixAudioSource(int targetRate) { _targetRate = targetRate; _loopback = new(targetRate); }
    public int SourceSampleRate => _targetRate;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, ref bool isRunning)
    {
        bool running = isRunning;

        bool loopRunning = running;
        var loopThread = new Thread(() => _loopback!.Start(buffer, signal, ref loopRunning))
            { IsBackground = true };
        loopThread.Start();

        var micDevice = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        micDevice.DataAvailable += (_, ev) =>
        {
            if (!running) return;
            int n = ev.BytesRecorded / 2;
            var batch = new float[n];
            for (int i = 0; i < n; i++)
                batch[i] = BitConverter.ToInt16(ev.Buffer, i * 2) / 32768f * 0.6f;
            buffer.Enqueue(batch);
            signal.Set();
        };
        micDevice.RecordingStopped += (_, _) => { running = false; signal.Set(); };
        micDevice.StartRecording();

        while (running) Thread.Sleep(100);
        isRunning = running;
        micDevice.StopRecording();
        micDevice.Dispose();
        loopThread.Join(2000);
    }

    public void Dispose() { _loopback?.Dispose(); }
}
