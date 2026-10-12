using Android.Content;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Droid;

/// <summary>
/// Android <see cref="IRecordingKeepAlive"/>: runs <see cref="RecordingService"/> (foreground
/// service of type "microphone" + partial wake lock) while recording, so AudioRecord keeps
/// delivering audio with the screen off or another app in front.
/// Registered as a singleton; the service reaches it through <see cref="Current"/> to forward
/// the notification's Mute / Stop actions.
/// </summary>
public sealed class AndroidRecordingKeepAlive : IRecordingKeepAlive
{
    private readonly object _gate = new();
    private RecordingNotice _notice;
    private bool _started;

    public AndroidRecordingKeepAlive() => Current = this;

    /// <summary>The DI singleton; <c>null</c> until the container creates it.</summary>
    internal static AndroidRecordingKeepAlive? Current { get; private set; }

    public event Action? StopRequested;

    public event Action? MuteToggleRequested;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    internal bool IsStarted
    {
        get { lock (_gate) return _started; }
    }

    /// <summary>Session start (Unix ms): the notification's chronometer counts from it.</summary>
    internal long StartedAtMillis
    {
        get { lock (_gate) return _startedAtMillis; }
    }

    private long _startedAtMillis;

    /// <summary>Latest notice, read by the service when it builds the notification.</summary>
    internal RecordingNotice Notice
    {
        get { lock (_gate) return _notice; }
    }

    public void Start(RecordingNotice notice)
    {
        lock (_gate)
        {
            _notice = notice;
            if (_started)
                return;
            _started = true;
            _startedAtMillis = Java.Lang.JavaSystem.CurrentTimeMillis();
        }

        var context = global::Android.App.Application.Context;
        try
        {
            // Android 14+ only allows this while the app is visible — Start is called from
            // the Record tap, so it is. Android 12+ throws ForegroundServiceStartNotAllowedException
            // (an IllegalStateException) otherwise.
            ContextCompat.StartForegroundService(
                context, new Intent(context, typeof(RecordingService)).SetAction(RecordingService.ActionStart));
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalStateException or Java.Lang.SecurityException)
        {
            // Recording still works while the app stays in the foreground.
            Console.WriteLine($"[VoiceType] Recording keep-alive service not started: {ex.Message}");
        }
    }

    public void Update(RecordingNotice notice)
    {
        lock (_gate)
        {
            if (!_started || notice == _notice)
                return;
            _notice = notice;

            // Under the lock so a concurrent Stop cannot be followed by a stale re-post.
            RecordingService.Notify(global::Android.App.Application.Context, notice, _startedAtMillis);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
                return;
            _started = false;

            // Also removes a notification posted by Update when the service never reached
            // the foreground.
            NotificationManagerCompat.From(global::Android.App.Application.Context)
                ?.Cancel(RecordingService.NotificationId);
        }

        // A running service leaves the foreground and stops itself. A service that is still
        // starting sees IsStarted == false in OnStartCommand and stops right after its
        // mandatory StartForeground call (stopping before it would crash on some versions).
        RecordingService.StopRunning();
    }

    internal void RaiseStopRequested()
    {
        if (IsStarted)
            StopRequested?.Invoke();
    }

    internal void RaiseMuteToggleRequested()
    {
        if (IsStarted)
            MuteToggleRequested?.Invoke();
    }
}
