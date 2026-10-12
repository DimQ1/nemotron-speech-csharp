using System.Globalization;

namespace VoiceType.Uno.History;

/// <summary>
/// Turns the stored layer (channels) into what the UI, exports, search and chat show (people): names, colours, the user's
/// overrides and the talk statistics. Pure.
/// </summary>
public static class SpeakerViews
{
    /// <summary>
    /// The person of every word of every layer segment: the run's channel → person map, then the user's overrides
    /// (<see cref="SpeakerOverrideInfo"/>, by time: a segment whose middle lies inside the range).
    /// </summary>
    public static SpeakerLayerView Build(
        RecordingManifest manifest, SpeakerLayerInfo layerInfo, SpeakerLayerDocument layer, DiarizationRunDocument? run)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(layerInfo);
        ArgumentNullException.ThrowIfNull(layer);

        var directory = manifest.Speakers ?? new SpeakerDirectoryInfo();
        var runInfo = manifest.FindRun(layerInfo.Run);
        var segments = new List<SegmentSpeakers>(layer.Segments.Count);
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var segment in layer.Segments)
        {
            var overridden = OverrideFor(directory, segment);
            var words = new List<string?>(segment.Words.Count);
            foreach (var channel in segment.Words)
            {
                var person = overridden ?? SpeakerDirectory.PersonOf(manifest, layerInfo.Run, channel);
                words.Add(person);
                if (person is not null)
                    used.Add(person);
            }
            var majority = overridden ?? SpeakerDirectory.PersonOf(manifest, layerInfo.Run, segment.Speaker);
            if (majority is not null)
                used.Add(majority);
            segments.Add(new SegmentSpeakers(
                segment.Index,
                majority,
                segment.Change == true && overridden is null,
                segment.Overlap is { Count: > 0 },
                words));
        }

        var stats = run is null ? [] : Stats(manifest, layerInfo.Run, run.Turns);
        foreach (var stat in stats)
            used.Add(stat.PersonId);

        var people = new List<SpeakerPerson>();
        for (var i = 0; i < directory.People.Count; i++)
        {
            var p = directory.People[i];
            if (used.Contains(p.Id))
                people.Add(new SpeakerPerson(p.Id, SpeakerDirectory.DisplayName(p, i), p.Color));
        }

        return new SpeakerLayerView(
            layerInfo.Id, layerInfo.Run, layerInfo.Version,
            runInfo?.Model?.Title ?? runInfo?.Model?.Folder ?? "Speaker detection",
            runInfo?.Mode?.Preset ?? "offline",
            people, segments, stats);
    }

    private static string? OverrideFor(SpeakerDirectoryInfo directory, SpeakerLayerSegment segment)
    {
        if (directory.Overrides.Count == 0)
            return null;
        var middle = (segment.Start + segment.End) / 2;
        // The newest override wins.
        for (var i = directory.Overrides.Count - 1; i >= 0; i--)
        {
            var o = directory.Overrides[i];
            if (middle >= o.From && middle <= o.To && directory.People.Any(p => p.Id == o.Person))
                return o.Person;
        }
        return null;
    }

    /// <summary>
    /// Talk time per person from the run's turns (a person's overlapping turns of merged channels count once), share of the
    /// total talk time, and number of turns (consecutive turns of one person closer than <paramref name="turnGapSeconds"/>
    /// count as one). Largest speaker first.
    /// </summary>
    public static IReadOnlyList<SpeakerStat> Stats(
        RecordingManifest manifest, string runId, IReadOnlyList<RunTurn> turns, double turnGapSeconds = 1.0)
    {
        var intervals = new Dictionary<string, List<(double Start, double End)>>(StringComparer.Ordinal);
        foreach (var turn in turns.OrderBy(t => t.Start))
        {
            var person = SpeakerDirectory.PersonOf(manifest, runId, turn.Speaker);
            if (person is null || turn.End <= turn.Start)
                continue;
            if (!intervals.TryGetValue(person, out var list))
                intervals[person] = list = [];
            list.Add((turn.Start, turn.End));
        }

        var totals = new List<(string Person, double Talk, int Turns)>();
        foreach (var (person, list) in intervals)
        {
            double talk = 0;
            var count = 0;
            var end = double.NegativeInfinity;
            foreach (var (s, e) in list)
            {
                if (s > end + turnGapSeconds)
                    count++;
                // Union length: time covered twice (merged channels) counts once.
                talk += Math.Max(0, e - Math.Max(s, end));
                end = Math.Max(end, e);
            }
            totals.Add((person, talk, count));
        }

        var sum = totals.Sum(t => t.Talk);
        return totals
            .OrderByDescending(t => t.Talk)
            .Select(t => new SpeakerStat(t.Person, Math.Round(t.Talk, 2), sum > 0 ? Math.Round(100 * t.Talk / sum, 1) : 0, t.Turns))
            .ToList();
    }

    /// <summary>Per run channel: talk time, words, turns, overlap, longest turn (the insights file).</summary>
    public static Dictionary<string, SpeakerChannelStats> ChannelStats(DiarizationRunDocument run, SpeakerLayerDocument layer)
    {
        var result = new Dictionary<string, SpeakerChannelStats>(StringComparer.Ordinal);
        SpeakerChannelStats Of(int channel)
        {
            var key = channel.ToString(CultureInfo.InvariantCulture);
            if (!result.TryGetValue(key, out var stats))
                result[key] = stats = new SpeakerChannelStats();
            return stats;
        }

        foreach (var turn in run.Turns.Where(t => t.Speaker >= 0))
        {
            var stats = Of(turn.Speaker);
            var length = Math.Max(0, turn.End - turn.Start);
            stats.TalkSec = Math.Round(stats.TalkSec + length, 2);
            stats.Turns++;
            stats.OverlapSec = Math.Round(stats.OverlapSec + (turn.OverlapSec ?? 0), 2);
            stats.LongestTurnSec = Math.Round(Math.Max(stats.LongestTurnSec, length), 2);
        }
        foreach (var segment in layer.Segments)
        {
            foreach (var channel in segment.Words.Where(c => c >= 0))
                Of(channel).Words++;
        }
        return result;
    }
}
