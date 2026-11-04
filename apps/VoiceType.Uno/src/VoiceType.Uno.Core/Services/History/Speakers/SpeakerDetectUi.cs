using VoiceType.Uno.Localization;

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
    public static string NoModelTitle => Loc.T("Speakers.Detect.NoModelTitle");
    public static string NoModelDetail => Loc.T("Speakers.Detect.NoModelDetail");
    public static string DownloadButton => Loc.T("Speakers.Detect.Download");
    public static string DownloadingTitle => Loc.T("Speakers.Detect.DownloadingTitle");
    public static string IdleTitle => Loc.T("Speakers.Detect.IdleTitle");
    public static string IdleDetail => Loc.T("Speakers.Detect.IdleDetail");

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
                return new SpeakerDetectInfo(SpeakerDetectKind.Failed, Loc.T("Speakers.Detect.FailedTitle"),
                    string.IsNullOrWhiteSpace(job.Error) ? Loc.T("Speakers.Detect.FailedDetail") : job.Error!, Loc.T("Speakers.Detect.TryAgain"), hasAudio, false, 0);
            if (job.Waiting)
                return new SpeakerDetectInfo(SpeakerDetectKind.Queued, Loc.T("Speakers.Detect.Detecting"), Loc.T("Speakers.Detect.Waiting"), Loc.T("Speakers.Detect.Queued"), false, true, 0);
            var percent = Math.Clamp(job.Progress, 0, 1);
            return new SpeakerDetectInfo(SpeakerDetectKind.Running, Loc.T("Speakers.Detect.Detecting"),
                percent > 0 ? Loc.F("Speakers.Percent", (percent * 100).ToString("0", Loc.Culture)) : Loc.T("Speakers.Detect.Starting"), Loc.T("Speakers.Detect.Running"), false, true, percent);
        }
        if (!modelAvailable && modelDownload is { } download)
            return new SpeakerDetectInfo(SpeakerDetectKind.DownloadingModel, DownloadingTitle,
                string.IsNullOrWhiteSpace(download.Text) ? Loc.T("Speakers.Detect.Starting") : download.Text, Loc.T("Speakers.Detect.Cancel"), true, true, Math.Clamp(download.Percent / 100d, 0, 1));
        if (!modelAvailable)
            return new SpeakerDetectInfo(SpeakerDetectKind.NoModel, NoModelTitle,
                string.IsNullOrWhiteSpace(modelError) ? NoModelDetail : Loc.F("Speakers.Detect.ModelErrorRetry", modelError), DownloadButton, true, false, 0);
        if (!hasAudio)
            return new SpeakerDetectInfo(SpeakerDetectKind.Idle, IdleTitle, Loc.T("Speakers.Detect.NoAudio"), Loc.T("Speakers.Detect.Button"), false, false, 0);
        return new SpeakerDetectInfo(SpeakerDetectKind.Idle, IdleTitle, IdleDetail, Loc.T("Speakers.Detect.Button"), true, false, 0);
    }

    /// <summary>Short chip text for a running detection (Versions tab, header).</summary>
    public static string ChipText(SpeakerDetectInfo info) =>
        info.Kind switch
        {
            SpeakerDetectKind.Queued => Loc.T("Speakers.Detect.ChipQueued"),
            SpeakerDetectKind.Running => info.Progress > 0 ? Loc.F("Speakers.Detect.ChipPercent", (info.Progress * 100).ToString("0", Loc.Culture)) : Loc.T("Speakers.Detect.ChipStarting"),
            _ => "",
        };

    /// <summary>Whether a queue job is a speaker detection (matched by name so it works before the enum value exists everywhere).</summary>
    public static bool IsDiarizeJob(HistoryJob job) => job.Kind.ToString() == "Diarize";
}

/// <summary>Snapshot of the running speaker-model download (percent 0..100 and its "42% · 44 / 106 MB" text).</summary>
public readonly record struct SpeakerModelDownload(double Percent, string? Text);

/// <summary>Snapshot of the tracked diarization job.</summary>
public readonly record struct SpeakerDetectJob(bool Waiting, bool Failed, double Progress, string? Error);
