namespace VoiceType.Uno.Services;

/// <summary>What the Record button and the state pill currently represent.</summary>
public enum RecordingPhase
{
    Idle,
    Recording,
    Muted,
    LoadingModel,
    DownloadingModel
}

/// <summary>Icon on the Record button.</summary>
public enum RecordGlyph
{
    /// <summary>Tapping starts (or resumes) listening.</summary>
    Microphone,

    /// <summary>Tapping stops the running session.</summary>
    Stop
}

/// <summary>
/// Turns the recording state into the strings of the main screen: the caption under the
/// Record button, the state pill, the button's accessible name and the notification title.
/// Split out of the ViewModel so the rules are testable without a UI.
/// </summary>
public static class RecordingStatusFormatter
{
    /// <summary>
    /// Model preparation wins over the recording flags: while the speech model downloads
    /// or loads the Record button cannot start a session.
    /// </summary>
    public static RecordingPhase GetPhase(bool isModelDownloading, bool isModelLoading, bool isRecording, bool isMuted) =>
        isModelDownloading ? RecordingPhase.DownloadingModel
        : isModelLoading ? RecordingPhase.LoadingModel
        : isRecording ? (isMuted ? RecordingPhase.Muted : RecordingPhase.Recording)
        : RecordingPhase.Idle;

    /// <summary>A muted session resumes on tap, so it shows the microphone; only a live session shows Stop.</summary>
    public static RecordGlyph GetGlyph(RecordingPhase phase) =>
        phase == RecordingPhase.Recording ? RecordGlyph.Stop : RecordGlyph.Microphone;

    /// <summary>Caption under the Record button.</summary>
    public static string GetCaption(RecordingPhase phase) => phase switch
    {
        RecordingPhase.Recording => "Tap to stop",
        RecordingPhase.Muted => "Muted · tap to resume",
        RecordingPhase.LoadingModel => "Loading model…",
        RecordingPhase.DownloadingModel => "Downloading model…",
        _ => "Tap to start"
    };

    /// <summary>Accessible name of the Record button (it has no visible text).</summary>
    public static string GetButtonName(RecordingPhase phase) => phase switch
    {
        RecordingPhase.Recording => "Stop recording",
        RecordingPhase.Muted => "Resume recording",
        RecordingPhase.LoadingModel => "Loading model…",
        RecordingPhase.DownloadingModel => "Downloading model…",
        _ => "Start recording"
    };

    /// <summary>State pill in the top bar: "Ready", "Rec 12:34", "Muted 12:34", …</summary>
    public static string GetPillText(RecordingPhase phase, TimeSpan elapsed) => phase switch
    {
        RecordingPhase.Recording => "Rec " + FormatElapsed(elapsed),
        RecordingPhase.Muted => "Muted " + FormatElapsed(elapsed),
        RecordingPhase.LoadingModel => "Loading model…",
        RecordingPhase.DownloadingModel => "Downloading…",
        _ => "Ready"
    };

    /// <summary>
    /// First line of the ongoing recording notification. It carries no elapsed time: the
    /// platform shows the session clock itself (Android chronometer), so the notification is
    /// re-posted only when the state changes — a per-second re-post makes the shade re-render
    /// under the finger and swallow taps on its Mute / Stop actions.
    /// </summary>
    public static string GetNoticeTitle(bool isMuted) => isMuted ? "Muted" : "Recording";

    /// <summary>Session length: "mm:ss" below one hour, "h:mm:ss" from then on. Negative reads as zero.</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        var totalHours = (long)elapsed.TotalHours;
        return totalHours > 0
            ? $"{totalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }
}
