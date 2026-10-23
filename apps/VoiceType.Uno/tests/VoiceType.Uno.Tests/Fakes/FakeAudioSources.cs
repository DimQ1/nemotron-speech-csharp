using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

namespace VoiceType.Uno.Tests;

/// <summary>
/// A capture source for tests: every few milliseconds it pushes a one-sample batch holding its <see cref="Tag"/>, until
/// its <see cref="CaptureState"/> stops. Can fail at start, fail later, and stop slowly.
/// </summary>
internal sealed class FakeAudioSource : IAudioSource
{
    public FakeAudioSource(CaptureMode mode, float tag)
    {
        Mode = mode;
        Tag = tag;
    }

    public CaptureMode Mode { get; }
    public float Tag { get; }
    public int SourceSampleRate => 16000;

    /// <summary>Thrown from <see cref="Start"/> before anything is captured.</summary>
    public Exception? FailAtStart { get; init; }

    /// <summary>Thrown from <see cref="Start"/> after this many batches.</summary>
    public int FailAfterBatches { get; init; } = -1;

    /// <summary>How long the source takes to wind down once asked to stop.</summary>
    public TimeSpan StopDelay { get; init; }

    /// <summary>Throws from <see cref="Start"/> when it is asked to stop (a device that complains while closing).</summary>
    public bool ThrowOnStop { get; init; }

    public volatile bool Started;
    public volatile bool Finished;
    public volatile bool Disposed;

    public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
    {
        if (FailAtStart is not null)
            throw FailAtStart;

        Started = true;
        var batches = 0;
        try
        {
            while (state.IsRunning)
            {
                buffer.Enqueue([Tag]);
                signal.Set();
                batches++;
                if (FailAfterBatches >= 0 && batches >= FailAfterBatches)
                    throw new InvalidOperationException($"{Mode} device was unplugged");
                state.Wait(3);
            }

            if (StopDelay > TimeSpan.Zero)
                Thread.Sleep(StopDelay);
            if (ThrowOnStop)
                throw new InvalidOperationException($"{Mode} device failed while closing");
        }
        finally
        {
            Finished = true;
        }
    }

    public void Dispose() => Disposed = true;
}

/// <summary>Hands out <see cref="FakeAudioSource"/>s and remembers them; a mode can be made to fail or to be unavailable.</summary>
internal sealed class FakeAudioSourceFactory : IAudioSourceFactory
{
    private readonly object _gate = new();
    private int _created;

    public List<FakeAudioSource> Sources { get; } = [];

    /// <summary>Modes whose <see cref="Create"/> throws (a platform without that source).</summary>
    public HashSet<CaptureMode> Unavailable { get; } = [];

    /// <summary>Modes whose source throws at start (a device that is missing or busy).</summary>
    public HashSet<CaptureMode> FailAtStart { get; } = [];

    public TimeSpan StopDelay { get; set; }

    /// <summary>Modes whose source complains while it is stopped.</summary>
    public HashSet<CaptureMode> ThrowOnStop { get; } = [];

    public IAudioSource Create(CaptureMode mode, int sampleRate)
    {
        if (Unavailable.Contains(mode))
            throw new InvalidOperationException($"{mode} is not available here.");

        lock (_gate)
        {
            var source = new FakeAudioSource(mode, ++_created)
            {
                FailAtStart = FailAtStart.Contains(mode) ? new InvalidOperationException($"{mode} could not be started.") : null,
                StopDelay = StopDelay,
                ThrowOnStop = ThrowOnStop.Contains(mode),
            };
            Sources.Add(source);
            return source;
        }
    }
}
