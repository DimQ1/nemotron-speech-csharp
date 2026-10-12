using System.Reflection;
using System.Runtime.InteropServices;
using SpeechLib.AudioStorage;
using VoiceType.Uno.History;
using AppInfo = VoiceType.Uno.History.AppInfo;

namespace VoiceType.Uno.Services;

/// <summary>Outcome of saving a live session to History.</summary>
/// <param name="RecordingId">Set when the recording was kept.</param>
/// <param name="Warning">Non-fatal problem to show (e.g. audio lost, text kept).</param>
public sealed record LiveRecordingOutcome(string? RecordingId, string? Warning)
{
    public bool Saved => RecordingId is not null;
}

/// <summary>
/// One live recording on its way to History (design §6.2): created at session start
/// (folder + <c>.lock</c> + manifest in state <c>recording</c>, so a crash is recovered),
/// fed by <see cref="RecognitionService"/> as an <see cref="IRecognitionTap"/> — every
/// recognized batch is teed into a crash-safe audio writer and timed by
/// <see cref="LiveSegmentTimer"/> — and finished off the UI thread on Stop.
/// <para>
/// Audio failures never cost the text: a writer that cannot be created or fails while
/// writing leaves a "text only" recording.
/// </para>
/// </summary>
public sealed class LiveRecordingSession : IRecognitionTap
{
    private readonly RecordingStore _store;
    private readonly AppSettings _settings;
    private readonly LiveRecognizerInfo _recognizer;
    private readonly LiveRecordingPlan _plan;
    private readonly ModelInfo _model;
    private readonly LiveSegmentTimer _timer;
    private IRecordingAudioWriter? _writer;
    private Exception? _audioError;
    private int _finished;
    private double? _lastLevelDb;

    private const double OnsetLeadSeconds = 0.5;

    private LiveRecordingSession(
        RecordingStore store,
        RecordingEntry entry,
        AppSettings settings,
        LiveRecognizerInfo recognizer,
        LiveRecordingPlan plan,
        ModelInfo model)
    {
        _store = store;
        _settings = settings;
        _recognizer = recognizer;
        _plan = plan;
        _model = model;
        RecordingId = entry.Id;
        FolderPath = entry.FolderPath;
        StartedAt = entry.Manifest.StartedAt;
        // Live partials trail speech onset by ~0.3–0.7 s (measured with Parakeet INT4 on
        // loopback playback of CV17 clips); the lead puts the segment start at the speech.
        _timer = new LiveSegmentTimer(
            recognizer.SampleRate, recognizer.UtteranceCommits, recognizer.CommitLagSeconds, onsetLeadSeconds: OnsetLeadSeconds);
    }

    public string RecordingId { get; }
    public string FolderPath { get; }
    public DateTimeOffset StartedAt { get; }

    /// <summary>Transcript field when the session began (UI thread).</summary>
    public string FieldAtStart { get; set; } = "";

    /// <summary>Manual input was on or the field was cleared during the session (UI thread).</summary>
    public bool UserTouched { get; set; }

    /// <summary>Set when audio could not be started; the session records text only.</summary>
    public string? AudioWarning => _audioError is null ? null : $"Audio is not being saved ({_audioError.Message}); the text will be.";

    /// <summary>
    /// Creates the recording folder and opens the audio writer. Synchronous file I/O:
    /// call it off the UI thread. Throws when the folder cannot be created.
    /// </summary>
    /// <param name="settings">Settings snapshot at session start.</param>
    public static LiveRecordingSession Start(
        RecordingStore store,
        AppSettings settings,
        LiveRecognizerInfo recognizer,
        LiveRecordingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(recognizer);
        ArgumentNullException.ThrowIfNull(plan);

        var quality = plan.Quality;
        var audio = new AudioInfo
        {
            File = HistoryAudioFormats.FileName(quality),
            Codec = HistoryAudioFormats.Codec(quality),
            SampleRate = recognizer.SampleRate,
            Bitrate = HistoryAudioFormats.BitrateBps(quality),
            Source = settings.AudioSource,
        };
        var entry = store.CreateRecording(DateTimeOffset.Now, plan.Mode, audio);

        var isDefault = string.Equals(
            Path.GetFileName(recognizer.ModelPath?.TrimEnd('/', '\\')), DefaultAsrModel.FolderName, StringComparison.OrdinalIgnoreCase);
        var model = LiveRecordingMetadata.DescribeModel(
            recognizer.ModelPath,
            recognizer.Family,
            isDefault ? DefaultAsrModel.RepoId : null,
            isDefault ? DefaultAsrModel.Title : null);

        var session = new LiveRecordingSession(store, entry, settings, recognizer, plan, model);
        try
        {
            session._writer = RecordingAudioWriterFactory.Create(
                Path.Combine(entry.FolderPath, "audio"),
                RecordingAudioFormat.Parse(plan.AudioFormatSpec),
                recognizer.SampleRate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            session._audioError = ex;
        }
        return session;
    }

    // ── IRecognitionTap (decode thread) ─────────────────────────────────────

    public void OnAudio(float[] batch)
    {
        _lastLevelDb = LiveSegmentTimer.LevelDb(batch);
        if (_writer is null)
            return;
        try
        {
            _writer.Append(batch);
        }
        catch (Exception ex)
        {
            _audioError ??= ex;
            _writer = DropWriter(_writer);
        }
    }

    public void OnMuted(int samples)
    {
        if (_writer is null)
            return;
        try
        {
            _writer.AddGap(samples);
        }
        catch (Exception ex)
        {
            _audioError ??= ex;
            _writer = DropWriter(_writer);
        }
    }

    public void OnStep(int samples, string? tail, string? committed) =>
        _timer.OnBatch(samples, tail, committed, _lastLevelDb);

    public void OnFlush(string? committed) => _timer.OnFlush(committed);

    // ── Finish / abandon ────────────────────────────────────────────────────

    /// <summary>
    /// Completes the audio, writes v1 (and a live edit as v2), finishes the manifest and
    /// applies retention. Call once, after <see cref="RecognitionService.Stopped"/>, off
    /// the UI thread. <paramref name="fieldAtEnd"/> is the transcript field after the
    /// final result was shown.
    /// </summary>
    public async Task<LiveRecordingOutcome> FinishAsync(string? fieldAtEnd, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            throw new InvalidOperationException("The recording was already finished.");

        var endedAt = DateTimeOffset.Now;
        var audio = await CompleteAudioAsync(cancellationToken).ConfigureAwait(false);

        var document = _timer.BuildDocument();
        var edited = LiveRecordingMetadata.DetectLiveEdit(FieldAtStart, fieldAtEnd, document.Text, UserTouched);
        if (!LiveRecordingMetadata.ShouldKeep(document.Text, edited, audio?.DurationSec ?? 0))
        {
            _store.DeleteRecording(RecordingId);
            return new LiveRecordingOutcome(null, null);
        }

        var spec = LiveRecordingMetadata.BuildLiveVersion(
            _settings, _recognizer, _model, _timer.DetectedLanguage, CurrentApp());
        var live = _store.AddVersion(RecordingId, spec, document);

        if (edited is not null)
        {
            // Typed during recording (design §5.2): v1 stays what was recognized, the
            // user's text becomes an "edited" version with edit.source = live.
            var edit = _store.SaveEdit(RecordingId, live.Id, edited);
            _store.Update(RecordingId, m =>
            {
                if (m.FindVersion(edit.Id) is { } v)
                {
                    v.ExtensionData ??= [];
                    foreach (var (key, value) in LiveRecordingMetadata.LiveEditExtension())
                        v.ExtensionData[key] = value;
                }
            });
        }

        if (audio is null)
            _store.Update(RecordingId, m => m.Audio = null);
        _store.FinishRecording(RecordingId, endedAt, audio);

        try
        {
            RetentionPolicy.Apply(_store, _settings.History, DateTimeOffset.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention runs again at the next start and after the next save.
        }

        var warning = _audioError is null ? null : $"Saved to History without audio: {_audioError.Message}";
        return new LiveRecordingOutcome(RecordingId, warning);
    }

    /// <summary>The session never started (capture failed): removes the folder and the writer.</summary>
    public async Task AbandonAsync()
    {
        if (Interlocked.Exchange(ref _finished, 1) != 0)
            return;
        if (_writer is not null)
        {
            try { await _writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* the folder goes anyway */ }
            _writer = null;
        }
        try
        {
            _store.DeleteRecording(RecordingId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            // Left for startup recovery.
        }
    }

    private async Task<AudioInfo?> CompleteAudioAsync(CancellationToken cancellationToken)
    {
        var writer = _writer;
        _writer = null;
        if (writer is null)
        {
            DeleteAudioFiles();
            return null;
        }

        try
        {
            var result = await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return LiveRecordingMetadata.BuildAudioInfo(
                Path.GetFileName(result.Path),
                _plan.Quality,
                result.SampleRate,
                result.Samples,
                result.Bytes,
                _settings.AudioSource,
                result.Gaps.Select(g => (g.AtSample, g.MutedSamples)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            _audioError ??= ex;
            DeleteAudioFiles();
            return null;
        }
        finally
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* completion already reported the failure */ }
        }
    }

    private static IRecordingAudioWriter? DropWriter(IRecordingAudioWriter writer)
    {
        _ = Task.Run(async () =>
        {
            try { await writer.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* already failed */ }
        });
        return null;
    }

    private void DeleteAudioFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(FolderPath, "audio.*"))
                File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray .part is finalized or ignored by startup recovery.
        }
    }

    private static AppInfo CurrentApp()
    {
        var assembly = typeof(LiveRecordingSession).Assembly;
        var version = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "ApplicationDisplayVersion")?.Value
            ?? assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString();
        return new AppInfo
        {
            Name = LiveRecordingMetadata.AppName,
            Version = LiveRecordingMetadata.CleanVersion(version),
            Platform = RuntimeInformation.RuntimeIdentifier,
        };
    }
}
