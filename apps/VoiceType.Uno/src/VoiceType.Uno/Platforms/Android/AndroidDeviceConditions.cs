using Android.Content;
using Android.OS;
using VoiceType.Uno.Services.Jobs;

namespace VoiceType.Uno.Droid;

/// <summary>
/// Android power state: the sticky <c>ACTION_BATTERY_CHANGED</c> intent for the current
/// state, <c>ACTION_POWER_CONNECTED</c> / <c>_DISCONNECTED</c> for changes. Both are delivered
/// to a context-registered receiver while the process lives.
/// </summary>
public sealed class AndroidDeviceConditions : IDeviceConditions
{
    private readonly PowerReceiver _receiver;
    private volatile bool _isCharging;

    public AndroidDeviceConditions()
    {
        _receiver = new PowerReceiver(this);
        var context = global::Android.App.Application.Context;
        var filter = new IntentFilter();
        filter.AddAction(Intent.ActionPowerConnected);
        filter.AddAction(Intent.ActionPowerDisconnected);
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            context.RegisterReceiver(_receiver, filter, ReceiverFlags.NotExported);
        else
            context.RegisterReceiver(_receiver, filter);
        _isCharging = ReadPlugged(context);
    }

    public bool HasBattery => true;
    public bool IsCharging => _isCharging;

    public event Action? Changed;

    private static bool ReadPlugged(Context context)
    {
        try
        {
            // Sticky broadcast: a null receiver returns the last value without registering.
            var battery = context.RegisterReceiver(null, new IntentFilter(Intent.ActionBatteryChanged));
            var plugged = battery?.GetIntExtra(BatteryManager.ExtraPlugged, 0) ?? 0;
            return plugged != 0;
        }
        catch (Java.Lang.Exception ex)
        {
            Console.WriteLine($"[VoiceType] Battery state unavailable: {ex.Message}");
            return false;
        }
    }

    private void OnPowerChanged(bool connected)
    {
        if (_isCharging == connected)
            return;
        _isCharging = connected;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        try
        {
            global::Android.App.Application.Context.UnregisterReceiver(_receiver);
        }
        catch (Java.Lang.IllegalArgumentException)
        {
        }
    }

    private sealed class PowerReceiver(AndroidDeviceConditions owner) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == Intent.ActionPowerConnected)
                owner.OnPowerChanged(true);
            else if (intent?.Action == Intent.ActionPowerDisconnected)
                owner.OnPowerChanged(false);
        }
    }
}
