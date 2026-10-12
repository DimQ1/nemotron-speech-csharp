using System.Globalization;
using System.Text.Json;

namespace VoiceType.Uno.History;

// Speaker diarization part of RecordingStore (design §10.4): runs, layers and analysis insights.
//   diarization/rM.json        model output of one run (turns + packed probabilities)
//   speakers/vN.rM.json        channel of every word of version N as labelled by run M
//   analysis/vN.sK.insights.json  per-speaker statistics
// Every file is written atomically, then the manifest; a crash in between leaves an orphan file, never a dangling entry.
public sealed partial class RecordingStore
{
    public const string DiarizationFolder = "diarization";
    public const string SpeakersFolder = "speakers";

    /// <summary>Path of the run file relative to the recording folder.</summary>
    public static string RunFile(string runId) => $"{DiarizationFolder}/{runId}.json";

    public static string InsightsFile(string versionId, string layerId) => $"{AnalysisStore.Folder}/{versionId}.{layerId}.insights.json";

    private static System.Text.Json.Serialization.Metadata.JsonTypeInfo<DiarizationRunInfo> RunTypeInfo =>
        HistoryJsonContext.Default.DiarizationRunInfo;

    /// <summary>
    /// Stores a finished run: writes <c>diarization/rM.json</c> and adds it to the manifest, registers its channels as people
    /// (names carried over from the previous run of the recording, <see cref="SpeakerMatcher"/>). <paramref name="spec"/>
    /// supplies model, mode, post-processing, settings, app and stats; the store assigns id, file and time.
    /// </summary>
    public DiarizationRunInfo AddDiarizationRun(string id, DiarizationRunInfo spec, DiarizationRunDocument document)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(document);

        DiarizationRunInfo? added = null;
        Mutate(id, (folder, m) =>
        {
            var run = HistoryJson.Clone(spec, RunTypeInfo);
            run.Id = NextId(m.DiarizationRuns.Select(r => r.Id), "r");
            run.File = RunFile(run.Id);
            run.CreatedAt = _time.GetLocalNow();
            run.Stats ??= new DiarizationRunStats();
            run.Stats.Speakers = document.Turns.Select(t => t.Speaker).Distinct().Count();

            var doc = HistoryJson.Clone(document, HistoryJsonContext.Default.DiarizationRunDocument);
            doc.Id = run.Id;
            HistoryJson.WriteAtomic(Path.Combine(folder, run.File), doc, HistoryJsonContext.Default.DiarizationRunDocument);
            m.DiarizationRuns.Add(run);
            SpeakerDirectory.ApplyRun(m, run, doc, previous => TryReadRunTurns(folder, previous));
            added = run;
        });
        return HistoryJson.Clone(added!, RunTypeInfo);
    }

    public DiarizationRunDocument LoadDiarizationRun(string id, string runId)
    {
        lock (_gate)
        {
            var folder = RequireFolder(id);
            var info = ReadManifest(folder).FindRun(runId)
                ?? throw new KeyNotFoundException($"Recording {id} has no diarization run {runId}.");
            return HistoryJson.Read(Path.Combine(folder, info.File), HistoryJsonContext.Default.DiarizationRunDocument);
        }
    }

    private static IReadOnlyList<RunTurn>? TryReadRunTurns(string folder, DiarizationRunInfo run)
    {
        try
        {
            return HistoryJson.Read(Path.Combine(folder, run.File), HistoryJsonContext.Default.DiarizationRunDocument).Turns;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a speaker layer. A layer of the same version, run and assigner version is replaced in place (same id and file);
    /// otherwise a new one is added. It becomes the version's current layer when <paramref name="makeCurrent"/> is set or the
    /// version had none.
    /// </summary>
    public SpeakerLayerInfo SaveSpeakerLayer(string id, SpeakerLayerInfo spec, SpeakerLayerDocument document, bool makeCurrent = true)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(document);

        SpeakerLayerInfo? saved = null;
        Mutate(id, (folder, m) =>
        {
            if (m.FindVersion(spec.Version) is null)
                throw new KeyNotFoundException($"Recording {id} has no version {spec.Version}.");
            if (m.FindRun(spec.Run) is null)
                throw new KeyNotFoundException($"Recording {id} has no diarization run {spec.Run}.");

            var stored = HistoryJson.Clone(spec, HistoryJsonContext.Default.SpeakerLayerInfo);
            var existing = m.SpeakerLayers.Find(l => l.Version == spec.Version && l.Run == spec.Run && l.Assigner == spec.Assigner);
            if (existing is null)
            {
                stored.Id = NextId(m.SpeakerLayers.Select(l => l.Id), "s");
                var suffix = m.SpeakerLayers.Any(l => l.Version == spec.Version && l.Run == spec.Run) ? $".a{spec.Assigner}" : "";
                stored.File = $"{SpeakersFolder}/{spec.Version}.{spec.Run}{suffix}.json";
                stored.CreatedAt = _time.GetLocalNow();
                m.SpeakerLayers.Add(stored);
            }
            else
            {
                stored.Id = existing.Id;
                stored.File = existing.File;
                stored.CreatedAt = _time.GetLocalNow();
                m.SpeakerLayers[m.SpeakerLayers.IndexOf(existing)] = stored;
            }

            var doc = HistoryJson.Clone(document, HistoryJsonContext.Default.SpeakerLayerDocument);
            doc.Version = stored.Version;
            doc.Run = stored.Run;
            doc.Assigner = stored.Assigner;
            doc.WordTiming = stored.WordTiming;
            HistoryJson.WriteAtomic(Path.Combine(folder, stored.File), doc, HistoryJsonContext.Default.SpeakerLayerDocument);

            if (makeCurrent || !m.CurrentSpeakers.ContainsKey(stored.Version))
                m.CurrentSpeakers[stored.Version] = stored.Id;
            saved = stored;
        });
        return HistoryJson.Clone(saved!, HistoryJsonContext.Default.SpeakerLayerInfo);
    }

    public SpeakerLayerDocument LoadSpeakerLayer(string id, string layerId)
    {
        lock (_gate)
        {
            var folder = RequireFolder(id);
            var info = ReadManifest(folder).FindSpeakerLayer(layerId)
                ?? throw new KeyNotFoundException($"Recording {id} has no speaker layer {layerId}.");
            return HistoryJson.Read(Path.Combine(folder, info.File), HistoryJsonContext.Default.SpeakerLayerDocument);
        }
    }

    public RecordingManifest SetCurrentSpeakerLayer(string id, string versionId, string layerId) =>
        Mutate(id, (_, m) =>
        {
            var layer = m.FindSpeakerLayer(layerId);
            if (layer is null || layer.Version != versionId)
                throw new KeyNotFoundException($"Recording {id} has no speaker layer {layerId} for version {versionId}.");
            m.CurrentSpeakers[versionId] = layerId;
        });

    /// <summary>Deletes one layer; the version's current layer falls back to its newest remaining one.</summary>
    public RecordingManifest DeleteSpeakerLayer(string id, string layerId) =>
        Mutate(id, (folder, m) =>
        {
            var layer = m.FindSpeakerLayer(layerId)
                ?? throw new KeyNotFoundException($"Recording {id} has no speaker layer {layerId}.");
            DeleteFileQuietly(Path.Combine(folder, layer.File));
            m.SpeakerLayers.Remove(layer);
            DropCurrentPointer(m, layer);
        });

    /// <summary>
    /// Deletes a run and the layers made from it. A run that a current layer stands on cannot be deleted (like the current
    /// version): delete or re-point the layer first.
    /// </summary>
    /// <exception cref="InvalidOperationException">A version's current layer comes from this run.</exception>
    public RecordingManifest DeleteDiarizationRun(string id, string runId) =>
        Mutate(id, (folder, m) =>
        {
            var run = m.FindRun(runId)
                ?? throw new KeyNotFoundException($"Recording {id} has no diarization run {runId}.");
            if (m.CurrentSpeakers.Values.Any(layerId => m.FindSpeakerLayer(layerId)?.Run == runId)
                || m.Versions.Any(v => m.CurrentSpeakerLayer(v.Id)?.Run == runId))
                throw new InvalidOperationException("The run labels a current version; make another detection current first.");

            foreach (var layer in m.SpeakerLayers.Where(l => l.Run == runId).ToList())
            {
                DeleteFileQuietly(Path.Combine(folder, layer.File));
                m.SpeakerLayers.Remove(layer);
            }
            DeleteFileQuietly(Path.Combine(folder, run.File));
            m.DiarizationRuns.Remove(run);
            if (m.Speakers is { } directory)
            {
                directory.Map.Remove(runId);
                directory.Suggested.RemoveAll(s => s.Run == runId);
            }
        });

    private static void DropCurrentPointer(RecordingManifest m, SpeakerLayerInfo layer)
    {
        if (m.CurrentSpeakers.TryGetValue(layer.Version, out var current) && current == layer.Id)
        {
            var next = m.SpeakerLayers.LastOrDefault(l => l.Version == layer.Version);
            if (next is null)
                m.CurrentSpeakers.Remove(layer.Version);
            else
                m.CurrentSpeakers[layer.Version] = next.Id;
        }
    }

    /// <summary>Writes <c>analysis/vN.sK.insights.json</c> and its manifest entry (replaces an earlier one for the same layer).</summary>
    public AnalysisInfo SaveInsights(string id, AnalysisInfo spec, SpeakerInsightsDocument document)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(document);

        AnalysisInfo? saved = null;
        Mutate(id, (folder, m) =>
        {
            var layerId = spec.SpeakerLayer ?? throw new ArgumentException("Insights need a speaker layer.", nameof(spec));
            var layer = m.FindSpeakerLayer(layerId)
                ?? throw new KeyNotFoundException($"Recording {id} has no speaker layer {layerId}.");
            var stored = HistoryJson.Clone(spec, HistoryJsonContext.Default.AnalysisInfo);
            var existing = m.Analyses.Find(a => a.SpeakerLayer == layerId);
            stored.Id = existing?.Id ?? NextId(m.Analyses.Select(a => a.Id), "a");
            stored.Version = layer.Version;
            stored.File = InsightsFile(layer.Version, layerId);
            stored.CreatedAt = _time.GetLocalNow();
            if (existing is null)
                m.Analyses.Add(stored);
            else
                m.Analyses[m.Analyses.IndexOf(existing)] = stored;

            var doc = HistoryJson.Clone(document, HistoryJsonContext.Default.SpeakerInsightsDocument);
            doc.Version = layer.Version;
            doc.SpeakerLayer = layerId;
            HistoryJson.WriteAtomic(Path.Combine(folder, stored.File), doc, HistoryJsonContext.Default.SpeakerInsightsDocument);
            saved = stored;
        });
        return HistoryJson.Clone(saved!, HistoryJsonContext.Default.AnalysisInfo);
    }

    public SpeakerInsightsDocument? LoadInsights(string id, string analysisId)
    {
        lock (_gate)
        {
            var folder = TryGetFolderCore(id);
            if (folder is null)
                return null;
            var info = ReadManifest(folder).Analyses.Find(a => a.Id == analysisId);
            if (info is null)
                return null;
            try
            {
                return HistoryJson.Read(Path.Combine(folder, info.File), HistoryJsonContext.Default.SpeakerInsightsDocument);
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>Layers, pointers and insights of a deleted version. Runs and people stay.</summary>
    private static void DeleteSpeakerArtifacts(string folder, RecordingManifest m, string versionId)
    {
        foreach (var layer in m.SpeakerLayers.Where(l => l.Version == versionId).ToList())
        {
            DeleteFileQuietly(Path.Combine(folder, layer.File));
            m.SpeakerLayers.Remove(layer);
        }
        m.CurrentSpeakers.Remove(versionId);
        foreach (var analysis in m.Analyses.Where(a => a.Version == versionId).ToList())
        {
            DeleteFileQuietly(Path.Combine(folder, analysis.File));
            m.Analyses.Remove(analysis);
        }
    }

    // ── People (manifest.speakers) ─────────────────────────────────────────

    public RecordingManifest RenamePerson(string id, string personId, string? name) =>
        Mutate(id, (_, m) => RequirePerson(SpeakerDirectory.Rename(m, personId, name), personId));

    public RecordingManifest SetPersonColor(string id, string personId, int color) =>
        Mutate(id, (_, m) => RequirePerson(SpeakerDirectory.SetColor(m, personId, color), personId));

    public RecordingManifest MergePeople(string id, string intoPersonId, string fromPersonId) =>
        Mutate(id, (_, m) => RequirePerson(SpeakerDirectory.Merge(m, intoPersonId, fromPersonId), intoPersonId + "/" + fromPersonId));

    /// <summary>Undoes the last merge into <paramref name="personId"/> (no-op when nothing was merged into it).</summary>
    public RecordingManifest SplitPerson(string id, string personId) =>
        Mutate(id, (_, m) => SpeakerDirectory.Split(m, personId));

    public RecordingManifest AddSpeakerOverride(string id, double from, double to, string personId) =>
        Mutate(id, (_, m) =>
        {
            if (m.Speakers?.People.Any(p => p.Id == personId) != true)
                throw new KeyNotFoundException($"Recording {id} has no speaker {personId}.");
            SpeakerDirectory.AddOverride(m, from, to, personId, _time.GetLocalNow());
        });

    private static void RequirePerson(bool ok, string what)
    {
        if (!ok)
            throw new KeyNotFoundException($"Speaker {what} not found.");
    }

    internal static string ChannelKey(int channel) => channel.ToString(CultureInfo.InvariantCulture);
}
