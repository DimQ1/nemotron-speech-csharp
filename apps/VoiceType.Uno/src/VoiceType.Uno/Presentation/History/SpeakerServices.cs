using System.Collections.Concurrent;
using Microsoft.UI.Xaml.Media;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// The History screens' access to the speaker service and the diarization job of a recording.
/// The service is registered by the app (<see cref="ISpeakerService"/>); in DEBUG builds a fake takes over
/// when the real one is absent. Without any service every speaker control stays hidden.
/// </summary>
internal static class SpeakerServices
{
    private static readonly ConcurrentDictionary<string, string> s_jobs = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, bool> s_colorWords = new(StringComparer.Ordinal);

    public static ISpeakerService? Current
    {
        get
        {
            try
            {
                return App.Services.GetService<ISpeakerService>();
            }
            catch (InvalidOperationException)
            {
                return null; // host not built (designer / tests)
            }
        }
    }

    public static IReprocessChainEnqueuer? Chain
    {
        get
        {
            try
            {
                return App.Services.GetService<IReprocessChainEnqueuer>() is { IsAvailable: true } chain ? chain : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <summary>Colour the words by speaker (a viewing choice kept for the session, like a toggle in a reader).</summary>
    public static bool ColorWords(string recordingId) => s_colorWords.GetValueOrDefault(recordingId, false);

    public static void SetColorWords(string recordingId, bool value) => s_colorWords[recordingId] = value;

    /// <summary>Enqueues detection and remembers the job so the screens can show its progress.</summary>
    public static string? Detect(string recordingId, string versionId, bool runNow)
    {
        if (Current is not { } service)
            return null;
        var id = service.EnqueueDiarize(recordingId, versionId, runNow);
        if (id is not null)
            s_jobs[recordingId] = id;
        return id;
    }

    /// <summary>The running, queued or failed detection of a recording, null when none is tracked.</summary>
    public static SpeakerDetectJob? TrackedJob(string recordingId)
    {
        try
        {
            var queue = HistoryServices.Jobs;
            // A job from the Queue (auto after Stop, a Reprocess chain) counts even when this screen did not start it.
            var active = queue.Jobs.FirstOrDefault(j => j.RecordingId == recordingId && SpeakerDetectUi.IsDiarizeJob(j) && !j.IsFinished);
            if (active is not null)
                return new SpeakerDetectJob(active.State is JobState.Queued or JobState.Waiting or JobState.Paused, false, active.Progress, null);
            if (s_jobs.TryGetValue(recordingId, out var id))
            {
                var job = queue.Get(id);
                if (job is null)
                    return new SpeakerDetectJob(false, false, 0, null); // not in the Queue (stand-in service): running until the layer shows up
                if (job.State == JobState.Failed)
                    return new SpeakerDetectJob(false, true, 0, job.Error);
                if (!job.IsFinished)
                    return new SpeakerDetectJob(job.State is JobState.Queued or JobState.Waiting or JobState.Paused, false, job.Progress, null);
                s_jobs.TryRemove(recordingId, out _);
            }
        }
        catch (InvalidOperationException)
        {
            // queue not available
        }
        return null;
    }

    public static void ClearTracked(string recordingId) => s_jobs.TryRemove(recordingId, out _);

    public static bool IsDark(FrameworkElement element) => element.ActualTheme == ElementTheme.Dark;

    public static Brush BrushFor(int colorIndex, FrameworkElement theme) => SpeakerBrushes.For(colorIndex, IsDark(theme));
}

internal static class SpeakerBrushes
{
    private static readonly Dictionary<(int, bool), SolidColorBrush> s_cache = [];

    public static SolidColorBrush For(int colorIndex, bool dark)
    {
        var index = SpeakersUi.NormalizeColor(colorIndex);
        if (!s_cache.TryGetValue((index, dark), out var brush))
        {
            var argb = SpeakersUi.ColorArgb(index, dark);
            brush = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            s_cache[(index, dark)] = brush;
        }
        return brush;
    }
}

/// <summary>Speaker name for a chat citation, looked up at render time from the layer (the citation itself is unchanged).</summary>
internal static class SpeakerCitations
{
    private static readonly object s_lock = new();
    private static string? s_key;
    private static List<double> s_starts = [];

    public static string? NameOf(ChatCitation citation)
    {
        if (SpeakerServices.Current is not { } service)
            return null;
        try
        {
            var store = HistoryServices.Recordings;
            var version = citation.VersionId ?? store.Get(citation.RecordingId)?.Manifest.CurrentVersion;
            if (version is null || service.GetLayer(citation.RecordingId, version) is not { } layer)
                return null;
            List<double> starts;
            lock (s_lock)
            {
                var key = citation.RecordingId + "|" + version;
                if (s_key != key)
                {
                    s_starts = [.. store.LoadTranscript(citation.RecordingId, version).Segments.Select(s => s.Start)];
                    s_key = key;
                }
                starts = s_starts;
            }
            var index = SpeakersUi.SegmentIndexAt(starts, citation.StartSec);
            var personId = layer.Segments.FirstOrDefault(s => s.SegmentIndex == index)?.PersonId;
            return SpeakersUi.PersonOf(layer, personId)?.Name;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
