using System.Globalization;
using System.Text.Json;

namespace VoiceType.Uno.History;

/// <summary>
/// <see cref="ISpeakerService"/> over the recording store and the job queue: reads the current layer of a version, queues
/// speaker detection, edits people. Names and colours live in <c>manifest.speakers</c> and are written only here (through
/// <see cref="RecordingStore"/>); the run and layer files are immutable.
/// </summary>
public sealed class SpeakerService : ISpeakerService
{
    private readonly RecordingStore _store;
    private readonly JobQueue _queue;
    private readonly IDiarizerFactory _factory;
    private readonly Func<HistorySettings> _settings;
    private readonly Action? _poke;
    private readonly HashSet<string> _labelling = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <param name="poke">Wakes the job runner after a job was queued (JobRunner.Poke); optional.</param>
    public SpeakerService(RecordingStore store, JobQueue queue, IDiarizerFactory factory, Func<HistorySettings> settings, Action? poke = null)
    {
        _store = store;
        _queue = queue;
        _factory = factory;
        _settings = settings;
        _poke = poke;
        _store.Changed += OnStoreChanged;
    }

    public event Action<string>? Changed;

    public bool IsModelAvailable => FindModel() is not null;

    public string ModelStatus
    {
        get
        {
            var model = FindModel();
            return model is null
                ? "Model not installed"
                : $"{model.Title} · {Math.Round(model.Bytes / 1_000_000.0).ToString(CultureInfo.InvariantCulture)} MB";
        }
    }

    private DiarizerModelRef? FindModel()
    {
        try
        {
            return _factory.Find(_settings().Speakers.ModelFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public SpeakerLayerView? GetLayer(string recordingId, string versionId)
    {
        try
        {
            var manifest = _store.Get(recordingId)?.Manifest;
            var info = manifest?.CurrentSpeakerLayer(versionId);
            if (manifest is null || info is null)
                return null;
            var layer = _store.LoadSpeakerLayer(recordingId, info.Id);
            return SpeakerViews.Build(manifest, info, layer, LoadRunTurns(recordingId, manifest, info.Run));
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A run file holds the packed probabilities too (about 480 KB of text per hour); the UI asks for the layer after every
    // change, so only the turns are kept, per file version.
    private readonly Dictionary<string, (long Stamp, DiarizationRunDocument Turns)> _runTurns = new(StringComparer.Ordinal);

    private DiarizationRunDocument? LoadRunTurns(string recordingId, RecordingManifest manifest, string runId)
    {
        try
        {
            var folder = _store.TryGetFolder(recordingId);
            var info = manifest.FindRun(runId);
            if (folder is null || info is null)
                return null;
            var file = new FileInfo(Path.Combine(folder, info.File));
            var stamp = file.LastWriteTimeUtc.Ticks ^ file.Length;
            var key = recordingId + "/" + runId;
            lock (_gate)
            {
                if (_runTurns.TryGetValue(key, out var cached) && cached.Stamp == stamp)
                    return cached.Turns;
            }
            var full = _store.LoadDiarizationRun(recordingId, runId);
            var turnsOnly = new DiarizationRunDocument { Id = full.Id, DurationSec = full.DurationSec, Turns = full.Turns };
            lock (_gate)
                _runTurns[key] = (stamp, turnsOnly);
            return turnsOnly;
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return null; // without the run only the statistics are missing
        }
    }

    /// <summary>The layer record (word timing source, run) of a version, for "approximate" hints and the Versions list.</summary>
    public SpeakerLayerInfo? GetLayerInfo(string recordingId, string versionId) =>
        _store.Get(recordingId)?.Manifest.CurrentSpeakerLayer(versionId);

    public string? EnqueueDiarize(string recordingId, string versionId, bool runNow)
    {
        if (_queue.IsReadOnly || !IsModelAvailable)
            return null;
        var manifest = _store.Get(recordingId)?.Manifest;
        if (manifest is null || manifest.FindVersion(versionId) is null || !manifest.HasAudio)
            return null;

        var existing = _queue.Jobs.FirstOrDefault(j =>
            j.Kind == HistoryJobKind.Diarize && j.RecordingId == recordingId && !j.IsFinished
            && (j.Parameters.SourceVersion ?? manifest.CurrentVersion) == versionId);
        if (existing is not null)
        {
            if (runNow)
                _queue.SetCondition(existing.Id, JobCondition.Now());
            _poke?.Invoke();
            return null;
        }

        var job = _queue.Enqueue(HistoryJobKind.Diarize, recordingId,
            new JobParameters
            {
                SourceVersion = versionId,
                Diarization = new DiarizationJobParameters { Preset = "offline", ReuseExistingRun = true },
                Origin = runNow ? ReprocessOrigins.User : ReprocessOrigins.LiveFallback,
            },
            runNow ? JobCondition.Now() : JobCondition.WhenIdle(),
            runNow ? JobPriorities.Normal : JobPriorities.Low);
        _poke?.Invoke();
        return job.Id;
    }

    public void RenamePerson(string recordingId, string personId, string name) =>
        Edit(() => _store.RenamePerson(recordingId, personId, name));

    public void SetPersonColor(string recordingId, string personId, int color) =>
        Edit(() => _store.SetPersonColor(recordingId, personId, color));

    public void MergePeople(string recordingId, string intoPersonId, string fromPersonId) =>
        Edit(() => _store.MergePeople(recordingId, intoPersonId, fromPersonId));

    public void SplitPerson(string recordingId, string personId) =>
        Edit(() => _store.SplitPerson(recordingId, personId));

    /// <summary>"Change speaker…" of a segment: the whole time range goes to <paramref name="personId"/> (survives new versions).</summary>
    public void ChangeSegmentSpeaker(string recordingId, double from, double to, string personId) =>
        Edit(() => _store.AddSpeakerOverride(recordingId, from, to, personId));

    private static void Edit(Func<RecordingManifest> edit)
    {
        try
        {
            edit();
        }
        catch (KeyNotFoundException)
        {
            // The person or recording is gone (deleted elsewhere): nothing to change.
        }
    }

    // ── A new version gets the labels of the latest run ─────────────────────

    private void OnStoreChanged(string recordingId)
    {
        Changed?.Invoke(recordingId);
        EnsureCurrentVersionLabelled(recordingId);
    }

    /// <summary>
    /// A version made after the last run (an edit, or a re-recognition that was made current) is labelled from that run without
    /// the model (design §10.4: "a new version automatically gets a layer from the latest run"). Runs in the background.
    /// </summary>
    private void EnsureCurrentVersionLabelled(string recordingId)
    {
        RecordingManifest? manifest;
        try
        {
            manifest = _store.Get(recordingId)?.Manifest;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return;
        }
        if (manifest?.CurrentVersion is not { } version || manifest.DiarizationRuns.Count == 0
            || manifest.State == RecordingStates.Recording || manifest.SpeakerLayers.Any(l => l.Version == version))
            return;

        var key = recordingId + "@" + version;
        lock (_gate)
        {
            if (!_labelling.Add(key))
                return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var latest = _store.Get(recordingId)?.Manifest.DiarizationRuns.LastOrDefault();
                if (latest is null || _store.Get(recordingId)?.Manifest.SpeakerLayers.Any(l => l.Version == version) == true)
                    return;
                var run = _store.LoadDiarizationRun(recordingId, latest.Id);
                var transcript = _store.LoadTranscript(recordingId, version);
                var built = SpeakerLayerBuilder.Build(run, transcript, version);
                _store.SaveSpeakerLayer(recordingId, new SpeakerLayerInfo
                {
                    Version = version,
                    Run = latest.Id,
                    Assigner = SpeakerLayerBuilder.AssignerVersion,
                    WordTiming = built.WordTiming,
                }, built.Document);
            }
            catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.WriteLine($"[VoiceType.Uno] Speaker layer for {key} was not built: {ex.Message}");
            }
            finally
            {
                lock (_gate)
                    _labelling.Remove(key);
            }
        });
    }
}

/// <summary>Speaker names of one transcript version as the exports, search and chat context use them.</summary>
public static class SpeakerLabels
{
    /// <summary>Segment index → name of the segment's main speaker; null when the version has no speaker layer.</summary>
    public static IReadOnlyDictionary<int, string>? ForVersion(RecordingStore store, string recordingId, string versionId)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            var manifest = store.Get(recordingId)?.Manifest;
            var info = manifest?.CurrentSpeakerLayer(versionId);
            if (manifest is null || info is null)
                return null;
            return ForLayer(manifest, info, store.LoadSpeakerLayer(recordingId, info.Id));
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IReadOnlyDictionary<int, string> ForLayer(RecordingManifest manifest, SpeakerLayerInfo info, SpeakerLayerDocument layer)
    {
        var view = SpeakerViews.Build(manifest, info, layer, run: null);
        var names = view.People.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
        var result = new Dictionary<int, string>();
        foreach (var segment in view.Segments)
        {
            if (segment.PersonId is { } id && names.TryGetValue(id, out var name))
                result[segment.SegmentIndex] = name;
        }
        return result;
    }

    /// <summary>Changes whenever what <see cref="ForVersion"/> returns can change (layer, names, merges, overrides); "" without a layer.</summary>
    public static string Signature(RecordingManifest? manifest, string versionId)
    {
        var layer = manifest?.CurrentSpeakerLayer(versionId);
        if (manifest is null || layer is null)
            return "";
        var directory = manifest.Speakers;
        var names = directory is null ? "" : string.Join('|', directory.People.Select(p => p.Id + "=" + p.Name));
        var map = directory is not null && directory.Map.TryGetValue(layer.Run, out var m)
            ? string.Join(',', m.Select(kv => kv.Key + ">" + kv.Value))
            : "";
        return $"{layer.Id}:{names}:{map}:{directory?.Overrides.Count ?? 0}";
    }

    /// <summary>Names the user gave (default "Speaker N" names are not searchable text).</summary>
    public static IReadOnlyList<string> GivenNames(RecordingManifest manifest) =>
        manifest.Speakers?.People.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name!).ToList() ?? [];
}

/// <summary>When a recording was saved: queue speaker detection or a whole Reprocess chain (design §11.5).</summary>
public static class SpeakerAutomation
{
    /// <summary>
    /// <see cref="ReprocessSettings.AutoNew"/> queues a chain for the new recording (and nothing else: it does the diarization);
    /// otherwise <see cref="SpeakerSettings.AutoAfterStop"/> = Best queues a background <c>diarize</c> job (when idle, low
    /// priority) if the model is installed and the recording is long enough. Never throws.
    /// </summary>
    /// <returns>True when something was queued.</returns>
    public static bool TryEnqueueAfterSave(
        ISpeakerService speakers, ReprocessChain? chain, RecordingStore store, string recordingId, HistorySettings settings, bool isPhone)
    {
        try
        {
            var manifest = store.Get(recordingId)?.Manifest;
            if (manifest is null || manifest.Mode.InjectText || manifest.CurrentVersion is not { } version || !manifest.HasAudio)
                return false;

            if (settings.Reprocess.AutoNew && chain is not null)
                return chain.EnqueueAuto(recordingId, settings.Reprocess).Count > 0;

            if (settings.Speakers.Effective(isPhone, speakers.IsModelAvailable) != SpeakerAutoMode.Best)
                return false;
            if (manifest.Audio!.DurationSec < settings.Reprocess.MinDurationSec)
                return false;
            return speakers.EnqueueDiarize(recordingId, version, runNow: false) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            Console.WriteLine($"[VoiceType.Uno] Speaker detection was not queued: {ex.Message}");
            return false;
        }
    }
}
