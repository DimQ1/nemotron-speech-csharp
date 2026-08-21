using VoiceType.Hotkeys.StatusNotifier;

namespace VoiceType.Uno.Services.Platform;

/// <summary>
/// Tray/taskbar recording indicator. Linux: StatusNotifierItem (AppIndicator)
/// over D-Bus; other platforms use <see cref="NullTrayIndicator"/>.
/// </summary>
public interface ITrayIndicator : IDisposable
{
    /// <summary>Raised when the user activates (clicks) the tray icon.</summary>
    event Action? Activated;

    /// <summary>Register the tray icon with the desktop environment.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Reflect recording state in the tray icon status/tooltip.</summary>
    void SetRecording(bool isRecording);
}

/// <summary>Null Object for platforms without a tray backend.</summary>
public sealed class NullTrayIndicator : ITrayIndicator
{
    public event Action? Activated { add { } remove { } }
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void SetRecording(bool isRecording) { }
    public void Dispose() { }
}

/// <summary>
/// Linux tray indicator backed by <see cref="StatusNotifierTrayIcon"/>.
/// Degrades to a no-op when the StatusNotifierWatcher is unavailable
/// (desktop environment without a system tray).
/// </summary>
public sealed class LinuxTrayIndicator : ITrayIndicator
{
    private const string ServiceName = "com.voicetype.uno.tray";

    private ITrayIconHandle? _tray;

    public event Action? Activated;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _tray = await StatusNotifierTrayIcon.TryCreateAsync(
                ServiceName,
                title: "VoiceType",
                iconName: "audio-input-microphone",
                cancellationToken)
            .ConfigureAwait(false);

        if (_tray is not null)
            _tray.Activated += () => Activated?.Invoke();
    }

    public void SetRecording(bool isRecording)
    {
        if (_tray is null)
            return;

        _tray.SetStatus(isRecording ? TrayStatus.NeedsAttention : TrayStatus.Active);
        _tray.SetTooltip(isRecording ? "VoiceType — Recording" : "VoiceType");
    }

    public void Dispose() => _tray?.Dispose();
}
