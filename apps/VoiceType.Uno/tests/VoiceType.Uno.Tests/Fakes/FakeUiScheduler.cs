using VoiceType.Uno.Services;

namespace VoiceType.Uno.Tests.Fakes;

/// <summary>
/// Deterministic <see cref="IUiScheduler"/>: posted actions run inline, and one-shot
/// timers only fire when a test ticks them. This is what makes the transcript
/// throttle testable without a dispatcher.
/// </summary>
internal sealed class FakeUiScheduler : IUiScheduler
{
    private readonly List<FakeUiTimer> _timers = [];

    /// <summary>The single timer the coordinator creates.</summary>
    public FakeUiTimer Timer => _timers.Single();

    public void Post(Action action) => action();

    public IUiTimer CreateOneShotTimer(TimeSpan interval, Action onTick)
    {
        var timer = new FakeUiTimer(interval, onTick);
        _timers.Add(timer);
        return timer;
    }
}

internal sealed class FakeUiTimer : IUiTimer
{
    private readonly Action _onTick;

    public FakeUiTimer(TimeSpan interval, Action onTick)
    {
        Interval = interval;
        _onTick = onTick;
    }

    public TimeSpan Interval { get; }

    /// <summary>How many times a window was opened — a throttle opens exactly one.</summary>
    public int StartCount { get; private set; }

    public bool IsRunning { get; private set; }

    public void Start()
    {
        StartCount++;
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;

    public void Dispose() => Stop();

    /// <summary>Fires the callback the way the dispatcher would for a one-shot timer.</summary>
    public void Tick()
    {
        IsRunning = false;
        _onTick();
    }
}
