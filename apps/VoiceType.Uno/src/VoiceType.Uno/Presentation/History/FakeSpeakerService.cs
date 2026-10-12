#if DEBUG
using System.Collections.Concurrent;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// DEBUG-only stand-in for <see cref="ISpeakerService"/> (registered with TryAdd, so the real one wins): an in-memory layer
/// with 3 speakers, overlap and word labels for the recordings of the debug seed (<c>VOICETYPE_DATA_ROOT</c> +
/// <c>VOICETYPE_SEED_CHATS=1</c>). Recordings titled "Q4 planning…" start without a layer so the Detect speakers flow
/// can be tried: it "runs" for a few seconds, then the layer appears. <c>VOICETYPE_SPEAKER_MODEL=missing</c> reports the
/// model as not installed.
/// </summary>
internal sealed class FakeSpeakerService : ISpeakerService
{
    private static readonly string[] DefaultNames = ["Anna", "Mark", "Speaker 3"];
    private static readonly int[] Pattern = [0, 1, 0, 1, 2, 0, 0, 1, 2, 2, 1, 0, 1, 0];

    private readonly RecordingStore _store;
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private int _jobs;

    public FakeSpeakerService(RecordingStore store) => _store = store;

    public event Action<string>? Changed;

    public bool IsModelAvailable =>
        !string.Equals(Environment.GetEnvironmentVariable("VOICETYPE_SPEAKER_MODEL"), "missing", StringComparison.OrdinalIgnoreCase);

    public string ModelStatus => IsModelAvailable ? "Nemotron 3 Diarization INT8 · 106 MB (debug stand-in)" : "Model not installed";

    public SpeakerLayerView? GetLayer(string recordingId, string versionId)
    {
        var state = StateOf(recordingId);
        if (!state.Detected)
            return null;
        TranscriptDocument transcript;
        try
        {
            transcript = _store.LoadTranscript(recordingId, versionId);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            return null;
        }

        lock (state)
        {
            string Resolve(string id)
            {
                while (state.Merged.TryGetValue(id, out var into))
                    id = into;
                return id;
            }

            var segments = new List<SegmentSpeakers>();
            var seconds = new Dictionary<string, double>();
            var turns = new Dictionary<string, int>();
            string? previous = null;
            for (var i = 0; i < transcript.Segments.Count; i++)
            {
                var segment = transcript.Segments[i];
                var main = Resolve("p" + Pattern[i % Pattern.Length]);
                var words = TextTokens.SplitWords(segment.Text).Count;
                var ids = new string?[words];
                var change = i % 4 == 2 && words >= 4;
                var other = Resolve("p" + ((Pattern[i % Pattern.Length] + 1) % 3));
                for (var w = 0; w < words; w++)
                    ids[w] = change && w >= words / 2 + 1 ? other : main;
                segments.Add(new SegmentSpeakers(i, main, change && other != main, i % 5 == 3, ids));
                seconds[main] = seconds.GetValueOrDefault(main) + Math.Max(0.5, segment.End - segment.Start);
                if (main != previous)
                    turns[main] = turns.GetValueOrDefault(main) + 1;
                previous = main;
            }

            var total = Math.Max(0.001, seconds.Values.Sum());
            var people = new List<SpeakerPerson>();
            for (var p = 0; p < 3; p++)
            {
                var id = "p" + p;
                if (state.Merged.ContainsKey(id))
                    continue;
                people.Add(new SpeakerPerson(id, state.Names.GetValueOrDefault(id, DefaultNames[p]), state.Colors.GetValueOrDefault(id, p)));
            }
            var stats = people
                .Select(p => new SpeakerStat(p.Id, seconds.GetValueOrDefault(p.Id), seconds.GetValueOrDefault(p.Id) / total * 100, turns.GetValueOrDefault(p.Id)))
                .ToList();
            return new SpeakerLayerView("s1", "r1", versionId, "Nemotron Diarization INT8", "offline, 30 s buffer", people, segments, stats);
        }
    }

    public string? EnqueueDiarize(string recordingId, string versionId, bool runNow)
    {
        var state = StateOf(recordingId);
        if (!IsModelAvailable || state.Running)
            return null;
        state.Running = true;
        var job = "fake-job-" + Interlocked.Increment(ref _jobs);
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            state.Detected = true;
            state.Running = false;
            Changed?.Invoke(recordingId);
        });
        return job;
    }

    public void RenamePerson(string recordingId, string personId, string name)
    {
        StateOf(recordingId).Names[personId] = name;
        Changed?.Invoke(recordingId);
    }

    public void SetPersonColor(string recordingId, string personId, int color)
    {
        StateOf(recordingId).Colors[personId] = SpeakersUi.NormalizeColor(color);
        Changed?.Invoke(recordingId);
    }

    public void MergePeople(string recordingId, string intoPersonId, string fromPersonId)
    {
        var state = StateOf(recordingId);
        if (intoPersonId != fromPersonId)
        {
            state.Merged[fromPersonId] = intoPersonId;
            state.Log.Add((fromPersonId, intoPersonId));
        }
        Changed?.Invoke(recordingId);
    }

    public void SplitPerson(string recordingId, string personId)
    {
        var state = StateOf(recordingId);
        var index = state.Log.FindLastIndex(m => m.Into == personId);
        if (index >= 0)
        {
            state.Merged.Remove(state.Log[index].From);
            state.Log.RemoveAt(index);
        }
        Changed?.Invoke(recordingId);
    }

    private State StateOf(string recordingId) =>
        _states.GetOrAdd(recordingId, id =>
        {
            var seeded = Environment.GetEnvironmentVariable("VOICETYPE_SEED_CHATS") == "1"
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VOICETYPE_DATA_ROOT"));
            var title = _store.Get(id)?.Manifest.EffectiveTitle ?? "";
            return new State { Detected = seeded && !title.StartsWith("Q4 planning", StringComparison.Ordinal) };
        });

    private sealed class State
    {
        public bool Detected;
        public bool Running;
        public Dictionary<string, string> Names { get; } = [];
        public Dictionary<string, int> Colors { get; } = [];
        public Dictionary<string, string> Merged { get; } = [];
        public List<(string From, string Into)> Log { get; } = [];
    }
}

/// <summary>DEBUG-only chain adapter: a Re-recognize job for the Speech step, then the fake speaker detection.</summary>
internal sealed class FakeReprocessChainEnqueuer : IReprocessChainEnqueuer
{
    private readonly JobQueue _queue;

    public FakeReprocessChainEnqueuer(JobQueue queue) => _queue = queue;

    public bool IsAvailable => !_queue.IsReadOnly;

    public IReadOnlyList<HistoryJob> Enqueue(ReprocessChainRequest request)
    {
        var jobs = new List<HistoryJob>();
        if (request.Recognize is { } recognize)
        {
            jobs.AddRange(request.RecordingIds.Count == 1
                ? [_queue.Enqueue(HistoryJobKind.Rerecognize, request.RecordingIds[0], recognize, request.Condition)]
                : _queue.EnqueueBatch(HistoryJobKind.Rerecognize, request.RecordingIds, recognize, request.Condition));
        }
        if (request.Speakers)
        {
            foreach (var id in request.RecordingIds)
            {
                var version = HistoryServices.Recordings.Get(id)?.Manifest.CurrentVersion;
                if (version is not null)
                    SpeakerServices.Detect(id, version, runNow: false);
            }
        }
        return jobs;
    }
}
#endif
