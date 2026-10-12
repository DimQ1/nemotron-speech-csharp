using Android.Content;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using VoiceType.Uno.Services.Jobs;

namespace VoiceType.Uno.Droid;

/// <summary>
/// Android <see cref="IJobsKeepAlive"/>: runs <see cref="JobsForegroundService"/> while a history
/// job runs. Started by <see cref="JobRunner"/> when a job starts — Android 12+ refuses that while
/// the app is in the background (ForegroundServiceStartNotAllowedException); the job then still
/// runs as long as the process does and the notification is simply absent.
/// </summary>
public sealed class AndroidJobsKeepAlive : IJobsKeepAlive
{
    private readonly object _gate = new();
    private JobNotice _notice;
    private bool _started;

    public AndroidJobsKeepAlive() => Current = this;

    internal static AndroidJobsKeepAlive? Current { get; private set; }

    public event Action? PauseRequested;
    public event Action? CancelRequested;
    public event Action? TimedOut;

    internal bool IsStarted
    {
        get { lock (_gate) return _started; }
    }

    internal JobNotice Notice
    {
        get { lock (_gate) return _notice; }
    }

    public void Start(JobNotice notice)
    {
        lock (_gate)
        {
            _notice = notice;
            if (_started)
                return;
            _started = true;
        }

        var context = global::Android.App.Application.Context;
        try
        {
            ContextCompat.StartForegroundService(
                context, new Intent(context, typeof(JobsForegroundService)).SetAction(JobsForegroundService.ActionStart));
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            Console.WriteLine($"[VoiceType] Job keep-alive service not started: {ex.Message}");
        }
    }

    public void Update(JobNotice notice)
    {
        lock (_gate)
        {
            if (!_started || notice == _notice)
                return;
            _notice = notice;
            JobsForegroundService.Notify(global::Android.App.Application.Context, notice);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
                return;
            _started = false;
            NotificationManagerCompat.From(global::Android.App.Application.Context)
                ?.Cancel(JobsForegroundService.NotificationId);
        }
        JobsForegroundService.StopRunning();
    }

    internal void RaisePauseRequested()
    {
        if (IsStarted)
            PauseRequested?.Invoke();
    }

    internal void RaiseCancelRequested()
    {
        if (IsStarted)
            CancelRequested?.Invoke();
    }

    internal void RaiseTimedOut() => TimedOut?.Invoke();
}
