namespace VoiceType.Uno.Services;

/// <summary>
/// Abstraction over the UI dispatcher. Keeps the UI-free core (transcript
/// coordinator, settings store) free of <c>Microsoft.UI.Dispatching</c> so it can
/// be unit-tested with a deterministic scheduler.
/// </summary>
public interface IUiScheduler
{
    /// <summary>Queues <paramref name="action"/> to run on the UI thread.</summary>
    void Post(Action action);

    /// <summary>
    /// Creates a one-shot timer owned by the caller. The returned timer is not
    /// repeating: it must be started again for each window.
    /// </summary>
    IUiTimer CreateOneShotTimer(TimeSpan interval, Action onTick);
}

/// <summary>A single-shot timer that runs its callback on the UI thread.</summary>
public interface IUiTimer : IDisposable
{
    bool IsRunning { get; }

    void Start();

    void Stop();
}
