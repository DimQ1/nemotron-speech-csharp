namespace SpeechLib.Audio;

/// <summary>
/// Observer that receives audio level notifications (0.0 = silence, 1.0 = max).
/// </summary>
public interface IAudioLevelObserver
{
    /// <summary>Called when a new audio level sample is available.</summary>
    void OnAudioLevel(float level);
}

/// <summary>
/// Publishes audio level events to registered observers.
/// Thread-safe; observers are notified on the caller's thread.
/// </summary>
public sealed class AudioLevelMeter
{
    private readonly List<IAudioLevelObserver> _observers = new();
    private readonly object _gate = new();

    /// <summary>Register an observer. Returns an unsubscribe action.</summary>
    public IDisposable Subscribe(IAudioLevelObserver observer)
    {
        lock (_gate)
            _observers.Add(observer);
        return new Unsubscriber(_observers, observer, _gate);
    }

    /// <summary>Compute RMS level from a batch of float samples and notify observers.</summary>
    public void Publish(float[] samples)
    {
        if (samples.Length == 0) return;

        float sumSquares = 0;
        foreach (var s in samples)
            sumSquares += s * s;

        var rms = MathF.Sqrt(sumSquares / samples.Length);
        var level = Math.Clamp(rms, 0f, 1f);

        lock (_gate)
        {
            foreach (var observer in _observers)
            {
                try { observer.OnAudioLevel(level); }
                catch { /* Don't let one observer break others */ }
            }
        }
    }

    /// <summary>Compute RMS level from a span of samples (0.0 – 1.0) without notifying observers.</summary>
    public static float ComputeRms(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return 0f;

        float sumSquares = 0;
        foreach (var s in samples)
            sumSquares += s * s;

        return Math.Clamp(MathF.Sqrt(sumSquares / samples.Length), 0f, 1f);
    }

    /// <summary>Compute RMS level for a batch and notify observers only when there is signal.</summary>
    public void PublishIfActive(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0) return;
        var level = ComputeRms(samples);

        lock (_gate)
        {
            foreach (var observer in _observers)
            {
                try { observer.OnAudioLevel(level); }
                catch { /* Don't let one observer break others */ }
            }
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private readonly List<IAudioLevelObserver> _observers;
        private readonly IAudioLevelObserver? _observer;
        private readonly object _gate;

        public Unsubscriber(List<IAudioLevelObserver> observers, IAudioLevelObserver observer, object gate)
        {
            _observers = observers;
            _observer = observer;
            _gate = gate;
        }

        public void Dispose()
        {
            if (_observer is null) return;
            lock (_gate)
                _observers.Remove(_observer);
        }
    }
}
