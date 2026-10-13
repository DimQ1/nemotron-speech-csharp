using System.Globalization;

namespace VoiceType.Uno.History;

public enum SpeakerDetectKind
{
    /// <summary>No speaker service in this build: the controls stay hidden.</summary>
    Unavailable,

    /// <summary>The ONNX model is not installed: explain and offer a one-tap download.</summary>
    NoModel,

    /// <summary>The model is downloading (shared queue): progress and Cancel.</summary>
    DownloadingModel,

    /// <summary>Model installed, no layer yet: offer "Detect speakers".</summary>
    Idle,

    Queued,
    Running,
    Failed,

    /// <summary>A layer exists.</summary>
    Ready,
}

/// <summary>What the "Detect speakers" row shows.</summary>
public sealed record SpeakerDetectInfo(SpeakerDetectKind Kind, string Title, string Detail, string ButtonText, bool ButtonEnabled, bool ShowProgress, double Progress)
{
    public bool IsVisible => Kind is not (SpeakerDetectKind.Unavailable or SpeakerDetectKind.Ready);
}

/// <summary>State machine of the "Detect speakers" button (model missing → idle → running → ready).</summary>
public static class SpeakerDetectUi
{
    public const string NoModelTitle = "Speaker detection needs a model";
    public const string NoModelDetail = "Nemotron 3 Diarization is not installed yet. Download it once (about 106 MB); it runs on this device.";
    public const string DownloadButton = "Download";
    public const string DownloadingTitle = "Downloading the speaker model…";
    public const string IdleTitle = "Who spoke when";
    public const string IdleDetail = "Detect speakers on this device. The audio never leaves it.";

    /// <param name="serviceAvailable">An <see cref="ISpeakerService"/> is registered.</param>
    /// <param name="modelAvailable"><see cref="ISpeakerService.IsModelAvailable"/>.</param>
    /// <param name="hasLayer">The viewed version has a speaker layer.</param>
    /// <param name="hasAudio">Detection runs on the stored audio.</param>
    /// <param name="running">The tracked job: null when none is active.</param>
    /// <param name="modelDownload">The speaker model download in the shared queue, when one is running.</param>
    public static SpeakerDetectInfo Describe(bool serviceAvailable, bool modelAvailable, bool hasLayer, bool hasAudio,
        SpeakerDetectJob? running, SpeakerModelDownload? modelDownload = null, string? modelError = null)
    {
        if (!serviceAvailable)
            return new SpeakerDetectInfo(SpeakerDetectKind.Unavailable, "", "", "", false, false, 0);
        if (hasLayer)
            return new SpeakerDetectInfo(SpeakerDetectKind.Ready, "", "", "", false, false, 0);
        if (running is { } job)
        {
            if (job.Failed)
                return new SpeakerDetectInfo(SpeakerDetectKind.Failed, "Speaker detection failed",
                    string.IsNullOrWhiteSpace(job.Error) ? "Try again from the Queue." : job.Error!, "Try again", hasAudio, false, 0);
            if (job.Waiting)
                return new SpeakerDetectInfo(SpeakerDetectKind.Queued, "Detecting speakers…", "Waiting in the Queue", "Queued", false, true, 0);
            var percent = Math.Clamp(job.Progress, 0, 1);
            return new SpeakerDetectInfo(SpeakerDetectKind.Running, "Detecting speakers…",
                percent > 0 ? (percent * 100).ToString("0", CultureInfo.InvariantCulture) + " %" : "Starting", "Running", false, true, percent);
        }
        if (!modelAvailable && modelDownload is { } download)
            return new SpeakerDetectInfo(SpeakerDetectKind.DownloadingModel, DownloadingTitle,
                string.IsNullOrWhiteSpace(download.Text) ? "Starting" : download.Text, "Cancel", true, true, Math.Clamp(download.Percent / 100d, 0, 1));
        if (!modelAvailable)
            return new SpeakerDetectInfo(SpeakerDetectKind.NoModel, NoModelTitle,
                string.IsNullOrWhiteSpace(modelError) ? NoModelDetail : modelError + " Try again.", DownloadButton, true, false, 0);
        if (!hasAudio)
            return new SpeakerDetectInfo(SpeakerDetectKind.Idle, IdleTitle, "The audio of this recording is gone, so speakers cannot be detected.", "Detect speakers", false, false, 0);
        return new SpeakerDetectInfo(SpeakerDetectKind.Idle, IdleTitle, IdleDetail, "Detect speakers", true, false, 0);
    }

    /// <summary>Short chip text for a running detection (Versions tab, header).</summary>
    public static string ChipText(SpeakerDetectInfo info) =>
        info.Kind switch
        {
            SpeakerDetectKind.Queued => "Speakers · queued",
            SpeakerDetectKind.Running => info.Progress > 0 ? "Speakers · " + (info.Progress * 100).ToString("0", CultureInfo.InvariantCulture) + " %" : "Speakers · starting",
            _ => "",
        };

    /// <summary>Whether a queue job is a speaker detection (matched by name so it works before the enum value exists everywhere).</summary>
    public static bool IsDiarizeJob(HistoryJob job) => job.Kind.ToString() == "Diarize";
}

/// <summary>Snapshot of the running speaker-model download (percent 0..100 and its "42% · 44 / 106 MB" text).</summary>
public readonly record struct SpeakerModelDownload(double Percent, string? Text);

/// <summary>Snapshot of the tracked diarization job.</summary>
public readonly record struct SpeakerDetectJob(bool Waiting, bool Failed, double Progress, string? Error);
