namespace VoiceType.Uno.Services;

/// <summary>What the ongoing "recording" notification shows.</summary>
/// <param name="Title">First line, e.g. "Recording 12:34" or "Muted 12:34".</param>
/// <param name="Text">Second line, e.g. "Parakeet TDT 0.6B v3 · Auto · → RU".</param>
/// <param name="IsMuted">Selects the Mute / Unmute action label.</param>
public readonly record struct RecordingNotice(string Title, string Text, bool IsMuted);

/// <summary>
/// Keeps microphone capture alive while the app is not in the foreground.
/// Android: a foreground service of type "microphone" with an ongoing notification
/// (Mute / Stop actions) and a partial wake lock. Other platforms: no-op.
/// <see cref="Start"/> must be called from the UI thread while the app is visible
/// (Android 14+ refuses to start a microphone foreground service from the background).
/// The events may be raised on any thread; subscribers marshal to the UI thread.
/// </summary>
public interface IRecordingKeepAlive
{
    /// <summary>The user pressed Stop in the notification.</summary>
    event Action? StopRequested;

    /// <summary>The user pressed Mute / Unmute in the notification.</summary>
    event Action? MuteToggleRequested;

    void Start(RecordingNotice notice);

    /// <summary>Refreshes the notification text; ignored when not started.</summary>
    void Update(RecordingNotice notice);

    void Stop();
}

/// <summary>Desktop heads capture in the background already — nothing to keep alive.</summary>
public sealed class NullRecordingKeepAlive : IRecordingKeepAlive
{
    public event Action? StopRequested { add { } remove { } }

    public event Action? MuteToggleRequested { add { } remove { } }

    public void Start(RecordingNotice notice) { }

    public void Update(RecordingNotice notice) { }

    public void Stop() { }
}
