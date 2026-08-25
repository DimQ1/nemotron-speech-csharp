using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace VoiceType.Uno.Droid;

[Activity(
    MainLauncher = true,
    ConfigurationChanges = global::Uno.UI.ActivityHelper.AllConfigChanges,
    WindowSoftInputMode = SoftInput.AdjustNothing | SoftInput.StateHidden
)]
public class MainActivity : Microsoft.UI.Xaml.ApplicationActivity
{
    private const int RuntimePermissionsRequestCode = 42;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        global::AndroidX.Core.SplashScreen.SplashScreen.InstallSplashScreen(this);

        base.OnCreate(savedInstanceState);

        RequestRuntimePermissions();
    }

    /// <summary>
    /// Requests the runtime permissions the dictation pipeline needs:
    /// RECORD_AUDIO for microphone capture (AudioRecord) and POST_NOTIFICATIONS
    /// (Android 13+) for the recording status notification.
    /// </summary>
    private void RequestRuntimePermissions()
    {
        var permissions = new List<string> { global::Android.Manifest.Permission.RecordAudio };
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            permissions.Add(global::Android.Manifest.Permission.PostNotifications);

        var missing = permissions
            .Where(p => global::AndroidX.Core.Content.ContextCompat.CheckSelfPermission(this, p)
                != global::Android.Content.PM.Permission.Granted)
            .ToArray();

        if (missing.Length > 0)
            global::AndroidX.Core.App.ActivityCompat.RequestPermissions(
                this, missing, RuntimePermissionsRequestCode);
    }

}
