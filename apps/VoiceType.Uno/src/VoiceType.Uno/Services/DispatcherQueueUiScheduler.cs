using Microsoft.UI.Dispatching;

namespace VoiceType.Uno.Services;

/// <summary>
/// <see cref="IUiScheduler"/> over the Uno/WinUI dispatcher queue, so the UI-free core
/// never references <c>Microsoft.UI.Dispatching</c>.
/// </summary>
public sealed class DispatcherQueueUiScheduler : IUiScheduler
{
    private readonly DispatcherQueue _dispatcher;

    public DispatcherQueueUiScheduler(DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.TryEnqueue(() => action());
    }

    public IUiTimer CreateOneShotTimer(TimeSpan interval, Action onTick)
    {
        ArgumentNullException.ThrowIfNull(onTick);
        return new DispatcherQueueUiTimer(_dispatcher, interval, onTick);
    }

    private sealed class DispatcherQueueUiTimer : IUiTimer
    {
        private readonly DispatcherQueueTimer _timer;

        public DispatcherQueueUiTimer(DispatcherQueue dispatcher, TimeSpan interval, Action onTick)
        {
            _timer = dispatcher.CreateTimer();
            _timer.Interval = interval;
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => onTick();
        }

        public bool IsRunning => _timer.IsRunning;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public void Dispose() => _timer.Stop();
    }
}
