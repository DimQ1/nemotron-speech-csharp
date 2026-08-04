#if ANDROID
using Android.App;
using Android.Content;
using Android.OS;

namespace LocalVoiceAssistant.Maui.Platforms.Android;

[Service(
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone)]
public sealed class LvaAudioForegroundService : Service
{
    private const int NotificationId = 4101;
    private const string ChannelId = "lva-audio";

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(
        Intent? intent,
        StartCommandFlags flags,
        int startId)
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(new NotificationChannel(
                ChannelId,
                "LVA microphone",
                NotificationImportance.Low));
        }

        var builder = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? new Notification.Builder(this, ChannelId)
            : new Notification.Builder(this);
        StartForeground(
            NotificationId,
            builder
                .SetContentTitle("LVA")
                .SetContentText("Локальное прослушивание активно")
                .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
                .Build());
        return StartCommandResult.Sticky;
    }

    public static void Start()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(LvaAudioForegroundService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }

    public static void Stop()
    {
        var context = global::Android.App.Application.Context;
        context.StopService(new Intent(context, typeof(LvaAudioForegroundService)));
    }
}
#endif
