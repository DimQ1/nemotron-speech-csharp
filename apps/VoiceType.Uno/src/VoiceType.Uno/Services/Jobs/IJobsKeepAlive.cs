namespace VoiceType.Uno.Services.Jobs;

/// <summary>What the ongoing "job running" notification shows.</summary>
/// <param name="Title">e.g. "Re-recognizing · Weekly sync".</param>
/// <param name="Text">e.g. "42% · ≈ 1 min left".</param>
/// <param name="Percent">0..100 for the progress bar; null = indeterminate.</param>
public readonly record struct JobNotice(string Title, string Text, int? Percent);

/// <summary>
/// Keeps the process alive while a heavy history job runs (design §9.5). Android: a foreground
/// service separate from the recording one, with Pause / Cancel actions. Desktop heads: no-op.
/// Start is called when a job starts; on Android it only succeeds while the app is visible (or
/// otherwise exempt) — the job still runs while the process lives.
/// </summary>
public interface IJobsKeepAlive
{
    /// <summary>The user pressed Pause in the notification.</summary>
    event Action? PauseRequested;

    /// <summary>The user pressed Cancel in the notification.</summary>
    event Action? CancelRequested;

    /// <summary>
    /// The system stopped the service (Android 15+ foreground-service time limit). The runner
    /// interrupts the job; it resumes from its checkpoint later.
    /// </summary>
    event Action? TimedOut;

    void Start(JobNotice notice);

    void Update(JobNotice notice);

    void Stop();
}

public sealed class NullJobsKeepAlive : IJobsKeepAlive
{
    public event Action? PauseRequested { add { } remove { } }
    public event Action? CancelRequested { add { } remove { } }
    public event Action? TimedOut { add { } remove { } }

    public void Start(JobNotice notice) { }
    public void Update(JobNotice notice) { }
    public void Stop() { }
}
