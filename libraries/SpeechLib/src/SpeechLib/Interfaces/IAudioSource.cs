using SpeechLib.Audio;

namespace SpeechLib;

public sealed class CaptureState : IDisposable
{
    private readonly ManualResetEventSlim _stopRequested = new(false);
    private int _isRunning = 1;

    /// <summary>Gets or sets whether a capture source should continue producing data.</summary>
    public bool IsRunning
    {
        get => Volatile.Read(ref _isRunning) != 0;
        set
        {
            Volatile.Write(ref _isRunning, value ? 1 : 0);
            if (value)
                _stopRequested.Reset();
            else
                _stopRequested.Set();
        }
    }

    /// <summary>Stops the source and wakes a source waiting for capture data.</summary>
    public void Stop() => IsRunning = false;

    /// <summary>Waits until the source is stopped or the interval elapses.</summary>
    public void Wait(int milliseconds) => _stopRequested.Wait(milliseconds);

    public void Dispose() => _stopRequested.Dispose();
}

/// <summary>Audio source abstraction for live capture.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Sample rate of the captured audio (before resampling).</summary>
    int SourceSampleRate { get; }

    /// <summary>
    /// Start capturing. Samples are pushed as <c>float[]</c> batches to <paramref name="buffer"/>
    /// and <paramref name="signal"/> is set whenever new data arrives.
    /// </summary>
    void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state);
}
