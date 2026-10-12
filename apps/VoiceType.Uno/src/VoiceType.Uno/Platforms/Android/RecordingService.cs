using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Droid;

/// <summary>
/// Foreground service of type "microphone" that keeps AudioRecord delivering audio while the
/// screen is off or another app is in front (Android silences the mic for background apps).
/// Holds a partial wake lock while running and shows the ongoing "Recording" notification with
/// Mute / Stop actions, which are forwarded to <see cref="AndroidRecordingKeepAlive"/>.
/// Started and stopped only through <see cref="AndroidRecordingKeepAlive"/>; it records nothing itself.
/// </summary>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeMicrophone)]
public sealed class RecordingService : Service
{
    internal const int NotificationId = 1001;
    internal const string ActionStart = "com.voicetype.uno.action.START_RECORDING";
    private const string ActionStop = "com.voicetype.uno.action.STOP_RECORDING";
    private const string ActionToggleMute = "com.voicetype.uno.action.TOGGLE_MUTE";
    private const string ChannelId = "recording";
    private const string WakeLockTag = "VoiceType:recording";

    private static readonly object s_channelGate = new();
    private static bool s_channelCreated;
    private static RecordingService? s_running;

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        Volatile.Write(ref s_running, this);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var keepAlive = AndroidRecordingKeepAlive.Current;
        switch (intent?.Action)
        {
            case ActionStop:
                // The view model reacts with IRecordingKeepAlive.Stop(), which stops this service.
                if (keepAlive is { IsStarted: true })
                    keepAlive.RaiseStopRequested();
                else
                    StopNow();
                break;

            case ActionToggleMute:
                if (keepAlive is { IsStarted: true })
                    keepAlive.RaiseMuteToggleRequested();
                else
                    StopNow();
                break;

            default:
                // StartForeground must follow startForegroundService within 5 seconds, even when
                // recording was stopped in the meantime — so enter the foreground first, then check.
                if (!EnterForeground(keepAlive?.Notice ?? new RecordingNotice("Recording", string.Empty, false))
                    || keepAlive is not { IsStarted: true })
                {
                    StopNow();
                    break;
                }

                AcquireWakeLock();
                break;
        }

        // Do not restart after the process is killed: the recording session died with it.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        ReleaseWakeLock();
        Interlocked.CompareExchange(ref s_running, null, this);
        base.OnDestroy();
    }

    /// <summary>Leaves the foreground and stops the running service, if any.</summary>
    internal static void StopRunning() => Volatile.Read(ref s_running)?.StopNow();

    /// <summary>Re-posts the notification under the same id (silent, alerts only once).</summary>
    internal static void Notify(Context context, RecordingNotice notice, long startedAtMillis)
    {
        try
        {
            var manager = NotificationManagerCompat.From(context);
            // POST_NOTIFICATIONS denied (Android 13+): nothing to post; the service still runs
            // and stays listed in the system's active-apps (Task Manager) panel.
            if (manager?.AreNotificationsEnabled() == true)
                manager.Notify(NotificationId, BuildNotification(context, notice, startedAtMillis));
        }
        catch (Java.Lang.RuntimeException ex)
        {
            Console.WriteLine($"[VoiceType] Recording notification not updated: {ex.Message}");
        }
    }

    private bool EnterForeground(RecordingNotice notice)
    {
        try
        {
            var startedAt = AndroidRecordingKeepAlive.Current?.StartedAtMillis is > 0 and var ms
                ? ms
                : Java.Lang.JavaSystem.CurrentTimeMillis();
            var notification = BuildNotification(this, notice, startedAt);
            // The "microphone" type exists from Android 11; older versions need no type.
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                ServiceCompat.StartForeground(this, NotificationId, notification, (int)ForegroundService.TypeMicrophone);
            else
                StartForeground(NotificationId, notification);
            return true;
        }
        catch (Java.Lang.RuntimeException ex)
        {
            // ForegroundServiceStartNotAllowedException (12+), SecurityException for a missing
            // FOREGROUND_SERVICE_MICROPHONE / RECORD_AUDIO grant (14+): recording continues
            // while the app is visible, it just is not kept alive in the background.
            Console.WriteLine($"[VoiceType] Recording keep-alive not in foreground: {ex.Message}");
            return false;
        }
    }

    private void StopNow()
    {
        ReleaseWakeLock();
        ServiceCompat.StopForeground(this, ServiceCompat.StopForegroundRemove);
        StopSelf();
    }

    private void AcquireWakeLock()
    {
        if (_wakeLock is not null)
            return;

        if (GetSystemService(PowerService) is not PowerManager power)
            return;

        // Keeps the CPU (capture + recognition) running with the screen off; held only while
        // the service runs and released in StopNow / OnDestroy.
        _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, WakeLockTag);
        _wakeLock!.SetReferenceCounted(false);
        _wakeLock.Acquire();
    }

    private void ReleaseWakeLock()
    {
        var wakeLock = _wakeLock;
        _wakeLock = null;
        if (wakeLock?.IsHeld == true)
            wakeLock.Release();
    }

    private static Notification BuildNotification(Context context, RecordingNotice notice, long startedAtMillis)
    {
        EnsureChannel(context);

        // Same as a launcher tap: brings the existing task to the front instead of a new activity.
        var open = new Intent(context, typeof(MainActivity));
        open.SetAction(Intent.ActionMain);
        open.AddCategory(Intent.CategoryLauncher);
        open.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop);

        var builder = new NotificationCompat.Builder(context, ChannelId);
        builder.SetSmallIcon(Resource.Drawable.ic_stat_recording);
        builder.SetContentTitle(notice.Title);
        builder.SetContentText(notice.Text);
        builder.SetOngoing(true);
        builder.SetOnlyAlertOnce(true);
        builder.SetSilent(true);
        // The system ticks the session clock itself (chronometer from the session start), so
        // the notification is re-posted only on state changes and its actions stay tappable.
        builder.SetWhen(startedAtMillis);
        builder.SetShowWhen(true);
        builder.SetUsesChronometer(true);
        builder.SetCategory(NotificationCompat.CategoryService);
        builder.SetVisibility(NotificationCompat.VisibilityPublic);
        // Android 12+ otherwise delays a foreground-service notification by up to 10 s.
        builder.SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate);
        builder.SetContentIntent(PendingIntent.GetActivity(context, 0, open, PendingFlags));
        builder.AddAction(0, notice.IsMuted ? "Unmute" : "Mute", ServiceIntent(context, ActionToggleMute, 1));
        builder.AddAction(0, "Stop", ServiceIntent(context, ActionStop, 2));
        return builder.Build()!;
    }

    private static PendingIntentFlags PendingFlags => PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent;

    private static PendingIntent? ServiceIntent(Context context, string action, int requestCode)
    {
        var intent = new Intent(context, typeof(RecordingService));
        intent.SetAction(action);
        return PendingIntent.GetService(context, requestCode, intent, PendingFlags);
    }

    private static void EnsureChannel(Context context)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(26))
            return;

        lock (s_channelGate)
        {
            if (s_channelCreated)
                return;

            var channel = new NotificationChannel(ChannelId, "Recording", NotificationImportance.Low)
            {
                Description = "Shown while VoiceType records from the microphone.",
            };
            channel.SetSound(null, null);
            channel.EnableVibration(false);
            channel.SetShowBadge(false);
            (context.GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
            s_channelCreated = true;
        }
    }
}
