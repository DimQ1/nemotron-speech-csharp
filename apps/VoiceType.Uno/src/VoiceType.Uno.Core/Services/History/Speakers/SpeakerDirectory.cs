using System.Globalization;

namespace VoiceType.Uno.History;

/// <summary>Assignment problem (maximum weight), by the Hungarian algorithm; sizes here are at most 8 x 8.</summary>
internal static class HungarianAssignment
{
    /// <summary>For each row the column it is assigned to, or -1. Maximises the summed weight.</summary>
    public static int[] Maximize(double[,] weight)
    {
        var rows = weight.GetLength(0);
        var cols = weight.GetLength(1);
        var n = Math.Max(rows, cols);
        var max = 0.0;
        for (var i = 0; i < rows; i++)
            for (var j = 0; j < cols; j++)
                max = Math.Max(max, weight[i, j]);

        // Minimise cost = max - weight on a square matrix (padding costs max, i.e. weight 0).
        var cost = new double[n + 1, n + 1];
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= n; j++)
                cost[i, j] = i <= rows && j <= cols ? max - weight[i - 1, j - 1] : max;

        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = new double[n + 1];
            Array.Fill(minv, double.PositiveInfinity);
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = double.PositiveInfinity;
                var j1 = 0;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j])
                        continue;
                    var cur = cost[i0, j] - u[i0] - v[j];
                    if (cur < minv[j])
                    {
                        minv[j] = cur;
                        way[j] = j0;
                    }
                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }
                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }
                j0 = j1;
            } while (p[j0] != 0);
            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            } while (j0 != 0);
        }

        var result = new int[rows];
        Array.Fill(result, -1);
        for (var j = 1; j <= n; j++)
        {
            if (p[j] >= 1 && p[j] <= rows && j <= cols)
                result[p[j] - 1] = j - 1;
        }
        return result;
    }
}

/// <summary>
/// Carries people over from one run to the next on the same recording (design §10.6): the 8 x 8 matrix of shared speech
/// time between the channels of the two runs is solved with the Hungarian algorithm; a channel of the new run inherits the
/// person of its partner when the shared time covers at least <see cref="MinOverlapShare"/> of its own speech.
/// </summary>
public static class SpeakerMatcher
{
    public const double MinOverlapShare = 0.6;
    private const int N = SpeechLib.Diarization.DiarizationConfig.NumSpeakers;

    /// <returns>New channel → previous channel it continues; channels without a partner are absent.</returns>
    public static Dictionary<int, int> Match(IReadOnlyList<RunTurn> previous, IReadOnlyList<RunTurn> current, double minShare = MinOverlapShare)
    {
        var shared = new double[N, N];
        var speech = new double[N];
        foreach (var c in current)
        {
            if ((uint)c.Speaker >= N)
                continue;
            speech[c.Speaker] += Math.Max(0, c.End - c.Start);
            foreach (var p in previous)
            {
                if ((uint)p.Speaker >= N)
                    continue;
                var overlap = Math.Min(p.End, c.End) - Math.Max(p.Start, c.Start);
                if (overlap > 0)
                    shared[p.Speaker, c.Speaker] += overlap;
            }
        }

        var assignment = HungarianAssignment.Maximize(shared);
        var result = new Dictionary<int, int>();
        for (var p = 0; p < N; p++)
        {
            var c = assignment[p];
            if (c >= 0 && speech[c] > 0 && shared[p, c] >= minShare * speech[c])
                result[c] = p;
        }
        return result;
    }
}

/// <summary>
/// The people of a recording and the channel → person map of its runs (<c>manifest.speakers</c>): arrival order, default
/// names "Speaker N", rename, colour, merge and split. All members mutate a manifest in place; call them inside
/// <see cref="RecordingStore.Update"/>. The run and layer files are never rewritten by these operations.
/// </summary>
public static class SpeakerDirectory
{
    public const int PaletteSize = 8;

    public static SpeakerDirectoryInfo Ensure(RecordingManifest manifest) => manifest.Speakers ??= new SpeakerDirectoryInfo();

    /// <summary>The name to show: the user's, else "Speaker N" by order of first appearance.</summary>
    public static string DisplayName(SpeakerPersonInfo person, int indexInPeople = 0) =>
        !string.IsNullOrWhiteSpace(person.Name)
            ? person.Name!
            : "Speaker " + (person.Number > 0 ? person.Number : indexInPeople + 1).ToString(CultureInfo.InvariantCulture);

    public static string DisplayName(SpeakerDirectoryInfo directory, string personId)
    {
        var i = directory.People.FindIndex(p => p.Id == personId);
        return i < 0 ? "Speaker" : DisplayName(directory.People[i], i);
    }

    public static string? PersonOf(RecordingManifest manifest, string runId, int channel) =>
        channel >= 0
        && manifest.Speakers?.Map.TryGetValue(runId, out var map) == true
        && map.TryGetValue(channel.ToString(CultureInfo.InvariantCulture), out var person)
            ? person
            : null;

    /// <summary>
    /// Registers a new run: its channels become people. A channel that continues a channel of the previous run (shared speech
    /// ≥ 60 %) gets that channel's person, with the name; every other channel becomes a new "Speaker N".
    /// </summary>
    public static void ApplyRun(
        RecordingManifest manifest, DiarizationRunInfo run, DiarizationRunDocument document,
        Func<DiarizationRunInfo, IReadOnlyList<RunTurn>?>? loadPreviousTurns = null)
    {
        var directory = Ensure(manifest);
        if (directory.Map.ContainsKey(run.Id))
            return;

        var previous = manifest.DiarizationRuns.LastOrDefault(r => r.Id != run.Id && directory.Map.ContainsKey(r.Id) && IsOlder(r, run));
        Dictionary<int, int> carried = [];
        if (previous is not null && loadPreviousTurns?.Invoke(previous) is { } previousTurns)
            carried = SpeakerMatcher.Match(previousTurns, document.Turns);

        // Arrival order = order of the first turn (the model already numbers channels that way).
        var channels = document.Turns.OrderBy(t => t.Start).Select(t => t.Speaker).Where(c => c >= 0).Distinct().ToList();

        var map = new Dictionary<string, string>();
        foreach (var channel in channels)
        {
            string? personId = null;
            if (carried.TryGetValue(channel, out var partner) && previous is not null
                && directory.Map[previous.Id].TryGetValue(partner.ToString(CultureInfo.InvariantCulture), out var inherited)
                && directory.People.Any(p => p.Id == inherited))
                personId = inherited;
            personId ??= NewPerson(manifest, directory).Id;
            map[channel.ToString(CultureInfo.InvariantCulture)] = personId;
        }
        directory.Map[run.Id] = map;
    }

    private static bool IsOlder(DiarizationRunInfo candidate, DiarizationRunInfo run) =>
        RunNumber(candidate.Id) < RunNumber(run.Id);

    private static int RunNumber(string id) =>
        id.Length > 1 && int.TryParse(id.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static SpeakerPersonInfo NewPerson(RecordingManifest manifest, SpeakerDirectoryInfo directory)
    {
        var maxNumber = Math.Max(
            directory.People.Count == 0 ? 0 : directory.People.Max(p => Math.Max(p.Number, 0)),
            directory.People.SelectMany(p => p.Merged ?? []).Select(m => m.Number).DefaultIfEmpty(0).Max());
        maxNumber = Math.Max(maxNumber, directory.People.Count);
        var person = new SpeakerPersonInfo
        {
            Id = RecordingStore.NextId(directory.People.Select(p => p.Id)
                .Concat(directory.People.SelectMany(p => p.Merged ?? []).Select(m => m.Id)), "p"),
            Number = maxNumber + 1,
            Color = FreeColor(directory),
        };
        directory.People.Add(person);
        return person;
    }

    private static int FreeColor(SpeakerDirectoryInfo directory)
    {
        var used = directory.People.Select(p => p.Color).ToHashSet();
        for (var c = 0; c < PaletteSize; c++)
        {
            if (!used.Contains(c))
                return c;
        }
        return directory.People.Count % PaletteSize;
    }

    public static bool Rename(RecordingManifest manifest, string personId, string? name)
    {
        var person = manifest.Speakers?.People.Find(p => p.Id == personId);
        if (person is null)
            return false;
        person.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        return true;
    }

    public static bool SetColor(RecordingManifest manifest, string personId, int color)
    {
        var person = manifest.Speakers?.People.Find(p => p.Id == personId);
        if (person is null)
            return false;
        person.Color = ((color % PaletteSize) + PaletteSize) % PaletteSize;
        return true;
    }

    /// <summary>
    /// Makes the channels of <paramref name="fromId"/> belong to <paramref name="intoId"/> (the detection itself does not
    /// change). The merged person is remembered inside the kept one so <see cref="Split"/> can restore it.
    /// </summary>
    public static bool Merge(RecordingManifest manifest, string intoId, string fromId)
    {
        var directory = manifest.Speakers;
        if (directory is null || intoId == fromId)
            return false;
        var into = directory.People.Find(p => p.Id == intoId);
        var fromIndex = directory.People.FindIndex(p => p.Id == fromId);
        if (into is null || fromIndex < 0)
            return false;
        var from = directory.People[fromIndex];

        var merged = new MergedPersonInfo { Id = from.Id, Name = from.Name, Color = from.Color, Number = from.Number, Index = fromIndex };
        foreach (var (run, map) in directory.Map)
        {
            foreach (var key in map.Where(kv => kv.Value == fromId).Select(kv => kv.Key).ToList())
            {
                map[key] = intoId;
                merged.Channels.Add(new SpeakerChannelRef { Run = run, Speaker = int.Parse(key, CultureInfo.InvariantCulture) });
            }
        }
        // What was merged into "from" earlier travels with it.
        if (from.Merged is { Count: > 0 })
            (into.Merged ??= []).AddRange(from.Merged);
        (into.Merged ??= []).Add(merged);
        foreach (var o in directory.Overrides.Where(o => o.Person == fromId))
            o.Person = intoId;
        directory.People.RemoveAt(fromIndex);
        return true;
    }

    /// <summary>Undoes the latest <see cref="Merge"/> into <paramref name="personId"/>: the person comes back with its channels.</summary>
    public static bool Split(RecordingManifest manifest, string personId)
    {
        var directory = manifest.Speakers;
        var person = directory?.People.Find(p => p.Id == personId);
        if (directory is null || person?.Merged is not { Count: > 0 } merged)
            return false;

        var restored = merged[^1];
        merged.RemoveAt(merged.Count - 1);
        if (merged.Count == 0)
            person.Merged = null;

        var back = new SpeakerPersonInfo { Id = restored.Id, Name = restored.Name, Color = restored.Color, Number = restored.Number };
        directory.People.Insert(Math.Clamp(restored.Index, 0, directory.People.Count), back);
        foreach (var channel in restored.Channels)
        {
            if (directory.Map.TryGetValue(channel.Run, out var map))
                map[channel.Speaker.ToString(CultureInfo.InvariantCulture)] = back.Id;
        }
        return true;
    }

    /// <summary>Records the user's "Change speaker" of a time range (survives a new version).</summary>
    public static void AddOverride(RecordingManifest manifest, double from, double to, string personId, DateTimeOffset at)
    {
        var directory = Ensure(manifest);
        directory.Overrides.RemoveAll(o => Math.Abs(o.From - from) < 0.01 && Math.Abs(o.To - to) < 0.01);
        directory.Overrides.Add(new SpeakerOverrideInfo { From = from, To = to, Person = personId, At = at });
    }
}
