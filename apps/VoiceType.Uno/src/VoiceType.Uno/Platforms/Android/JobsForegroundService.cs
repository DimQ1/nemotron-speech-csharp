using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.App;
using VoiceType.Uno.Services.Jobs;

namespace VoiceType.Uno.Droid;

/// <summary>
/// Foreground service that keeps the process (and a partial wake lock) alive while a history
/// job — on-device re-recognition of a stored recording — runs (design §9.5). Separate from
/// <see cref="RecordingService"/> (type microphone): it records nothing and only exists while
/// a job runs. Shows "Re-recognizing a recording · 42%" with Pause / Cancel, forwarded to
/// <see cref="AndroidJobsKeepAlive"/>.
/// <para>
/// Type: <c>mediaProcessing</c> on Android 15+ ("time-consuming operations on media assets"
/// — turning a stored recording into a transcript), <c>dataSync</c> on Android 10–14
/// (its documented uses include local file processing); both are declared. Both have a
/// 6 h per 24 h budget on Android 15+, after which <see cref="OnTimeout(int, ForegroundService)"/>
/// is called and the service must stop within seconds: the runner then interrupts the job,
/// which keeps its checkpoint and resumes later. <c>shortService</c> (3 min) is too short and
/// <c>specialUse</c> needs a Play review justification.
/// </para>
/// </summary>
// The manifest entry is written by hand in Platforms/Android/AndroidManifest.xml: the attribute's
// manifest generator drops TypeMediaProcessing and would declare only "dataSync".
[Service(Name = "com.voicetype.uno.JobsForegroundService", Exported = false)]
public sealed class JobsForegroundService : Service
{
    internal const int NotificationId = 1002;
    internal const string ActionStart = "com.voicetype.uno.action.START_JOB";
    private const string ActionPause = "com.voicetype.uno.action.PAUSE_JOB";
    private const string ActionCancel = "com.voicetype.uno.action.CANCEL_JOB";
    private const string ChannelId = "jobs";
    private const string WakeLockTag = "VoiceType:jobs";

    private static readonly object s_channelGate = new();
    private static bool s_channelCreated;
    private static JobsForegroundService? s_running;

    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override void OnCreate()
    {
        base.OnCreate();
        Volatile.Write(ref s_running, this);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var keepAlive = AndroidJobsKeepAlive.Current;
        switch (intent?.Action)
        {
            case ActionPause:
                if (keepAlive is { IsStarted: true })
                    keepAlive.RaisePauseRequested();
                else
                    StopNow();
                break;

            case ActionCancel:
                if (keepAlive is { IsStarted: true })
                    keepAlive.RaiseCancelRequested();
                else
                    StopNow();
                break;

            default:
                // startForeground must follow startForegroundService within a few seconds, even
                // when the job already ended — enter the foreground first, then check.
                if (!EnterForeground(keepAlive?.Notice ?? new JobNotice("Processing a recording", "", null))
                    || keepAlive is not { IsStarted: true })
                {
                    StopNow();
                    break;
                }
                AcquireWakeLock();
                break;
        }

        // A killed process re-queues its running job on the next start; nothing to restart here.
        return StartCommandResult.NotSticky;
    }

    /// <summary>Android 15+: the dataSync / mediaProcessing time budget is used up.</summary>
    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        AndroidJobsKeepAlive.Current?.RaiseTimedOut();
        StopNow();
    }

    public override void OnDestroy()
    {
        ReleaseWakeLock();
        Interlocked.CompareExchange(ref s_running, null, this);
        base.OnDestroy();
    }

    internal static void StopRunning() => Volatile.Read(ref s_running)?.StopNow();

    internal static void Notify(Context context, JobNotice notice)
    {
        try
        {
            var manager = NotificationManagerCompat.From(context);
            if (manager?.AreNotificationsEnabled() == true)
                manager.Notify(NotificationId, BuildNotification(context, notice));
        }
        catch (Java.Lang.RuntimeException ex)
        {
            Console.WriteLine($"[VoiceType] Job notification not updated: {ex.Message}");
        }
    }

    /// <summary>The foreground-service type for this Android version (0 = untyped, before Android 10).</summary>
    internal static ForegroundService ServiceType =>
        OperatingSystem.IsAndroidVersionAtLeast(35) ? ForegroundService.TypeMediaProcessing
        : OperatingSystem.IsAndroidVersionAtLeast(29) ? ForegroundService.TypeDataSync
        : 0;

    private bool EnterForeground(JobNotice notice)
    {
        try
        {
            var notification = BuildNotification(this, notice);
            if (OperatingSystem.IsAndroidVersionAtLeast(29))
                ServiceCompat.StartForeground(this, NotificationId, notification, (int)ServiceType);
            else
                StartForeground(NotificationId, notification);
            return true;
        }
        catch (Java.Lang.RuntimeException ex)
        {
            // ForegroundServiceStartNotAllowedException (12+, app not visible) or the time budget
            // is exhausted (15+): the job keeps running while the app is in front.
            Console.WriteLine($"[VoiceType] Job keep-alive not in foreground: {ex.Message}");
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
        if (_wakeLock is not null || GetSystemService(PowerService) is not PowerManager power)
            return;
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

    private static Notification BuildNotification(Context context, JobNotice notice)
    {
        EnsureChannel(context);

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
        builder.SetShowWhen(false);
        builder.SetCategory(NotificationCompat.CategoryProgress);
        builder.SetVisibility(NotificationCompat.VisibilityPublic);
        builder.SetForegroundServiceBehavior(NotificationCompat.ForegroundServiceImmediate);
        if (notice.Percent is { } percent)
            builder.SetProgress(100, Math.Clamp(percent, 0, 100), false);
        else
            builder.SetProgress(0, 0, true);
        builder.SetContentIntent(PendingIntent.GetActivity(context, 10, open, PendingFlags));
        builder.AddAction(0, "Pause", ServiceIntent(context, ActionPause, 11));
        builder.AddAction(0, "Cancel", ServiceIntent(context, ActionCancel, 12));
        return builder.Build()!;
    }

    private static PendingIntentFlags PendingFlags => PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent;

    private static PendingIntent? ServiceIntent(Context context, string action, int requestCode)
    {
        var intent = new Intent(context, typeof(JobsForegroundService));
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
            var channel = new NotificationChannel(ChannelId, "Background jobs", NotificationImportance.Low)
            {
                Description = "Shown while VoiceType re-recognizes recordings from the Queue.",
            };
            channel.SetSound(null, null);
            channel.EnableVibration(false);
            channel.SetShowBadge(false);
            (context.GetSystemService(NotificationService) as NotificationManager)?.CreateNotificationChannel(channel);
            s_channelCreated = true;
        }
    }
}
