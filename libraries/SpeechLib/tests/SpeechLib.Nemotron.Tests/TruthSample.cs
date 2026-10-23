using System.Text.Json;
using SpeechLib.Models;

namespace SpeechLib.Nemotron.Tests;

/// <summary>One word of the TTS ground truth: SAPI word start, end, and the clip (turn) and voice it belongs to.</summary>
internal sealed record TruthWord(string W, double S, double E, string Clip, string Voice);

/// <summary>
/// A synthetic two-voice dialogue (Windows SAPI voices, truth = the engine's own word positions, accurate to about 15 ms
/// for English and 25 ms for Russian words) with a scorer: start errors against the truth and the word-to-speaker accuracy
/// a PERFECT diarizer would give with <c>SpeakerAssigner</c>'s window rule, so every wrong label is caused by word timing.
/// </summary>
internal sealed class TruthSample
{
    private const double FrameSeconds = 0.01;

    public required string Language { get; init; }
    public required float[] Audio { get; init; }
    public required IReadOnlyList<TruthWord> Words { get; init; }

    public static TruthSample Load(string language)
    {
        var audio = NemotronTestEnvironment.LoadAudio(NemotronTestEnvironment.DataPath("truth", $"dialogue-{language}.wav"));
        using var json = JsonDocument.Parse(File.ReadAllText(NemotronTestEnvironment.DataPath("truth", $"dialogue-{language}.json")));
        var words = json.RootElement.GetProperty("words").EnumerateArray()
            .Select(w => new TruthWord(w.GetProperty("w").GetString()!, w.GetProperty("s").GetDouble(), w.GetProperty("e").GetDouble(),
                w.GetProperty("clip").GetString()!, w.GetProperty("voice").GetString()!))
            .ToList();
        return new TruthSample { Language = language, Audio = audio, Words = words };
    }

    /// <summary>Per matched word: signed start error in seconds (hypothesis minus truth), whether the speaker label was right, whether the word is at a turn edge.</summary>
    public sealed record Row(string Word, double StartError, double EndError, bool SpeakerCorrect, bool TurnEdge);

    public IReadOnlyList<Row> Score(IReadOnlyList<WordTiming> hypothesis)
    {
        var pairs = Align(Words.Select(w => Normalize(w.W)).ToList(), hypothesis.Select(w => Normalize(w.Word)).ToList());
        var labels = AssignSpeakers(hypothesis);
        var voices = Words.Select(w => w.Voice).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();

        var firstOfClip = new Dictionary<string, int>();
        var lastOfClip = new Dictionary<string, int>();
        for (var k = 0; k < Words.Count; k++)
        {
            firstOfClip.TryAdd(Words[k].Clip, k);
            lastOfClip[Words[k].Clip] = k;
        }

        return pairs.Select(p =>
        {
            var truth = Words[p.Truth];
            var hyp = hypothesis[p.Hyp];
            var edge = p.Truth - firstOfClip[truth.Clip] < 2 || lastOfClip[truth.Clip] - p.Truth < 2;
            return new Row(truth.W, hyp.StartSeconds - truth.S, hyp.EndSeconds - truth.E, labels[p.Hyp] == voices.IndexOf(truth.Voice), edge);
        }).ToList();
    }

    /// <summary>Port of <c>SpeakerAssigner.AssignWords</c> (window [start, min(end, start + 0.5 s)), 10 ms frames, top channel at least 0.5 or the previous word's speaker) with the true speaker timeline.</summary>
    private int[] AssignSpeakers(IReadOnlyList<WordTiming> hypothesis)
    {
        var voices = Words.Select(w => w.Voice).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();
        var duration = Math.Max(Words.Max(w => w.E), hypothesis.Count == 0 ? 0 : hypothesis.Max(w => w.EndSeconds)) + 1;
        var frames = (int)Math.Ceiling(duration / FrameSeconds) + 20;
        var active = voices.Select(_ => new bool[frames]).ToList();
        foreach (var turn in Words.GroupBy(w => w.Clip))
        {
            var voice = voices.IndexOf(turn.First().Voice);
            for (var f = (int)(turn.Min(w => w.S) / FrameSeconds); f < Math.Min(frames, (int)Math.Ceiling(turn.Max(w => w.E) / FrameSeconds)); f++)
                active[voice][f] = true;
        }

        var labels = new int[hypothesis.Count];
        var previous = -1;
        for (var i = 0; i < hypothesis.Count; i++)
        {
            var t0 = hypothesis[i].StartSeconds;
            var t1 = Math.Max(hypothesis[i].EndSeconds, t0);
            var end = Math.Max(Math.Min(t1, t0 + 0.5), t0 + 0.03);
            var f0 = Math.Max(0, (int)Math.Floor(t0 / FrameSeconds + 1e-9));
            var f1 = Math.Max(f0 + 1, (int)Math.Ceiling(end / FrameSeconds - 1e-9));
            f1 = Math.Min(f1, frames);
            var label = previous;
            if (f1 > f0)
            {
                var top = -1;
                var best = -1.0;
                for (var v = 0; v < voices.Count; v++)
                {
                    var mean = 0.0;
                    for (var f = f0; f < f1; f++)
                        mean += active[v][f] ? 1 : 0;
                    mean /= f1 - f0;
                    if (mean > best)
                    {
                        best = mean;
                        top = v;
                    }
                }
                label = best >= 0.5 ? top : previous;
            }
            labels[i] = label;
            if (label >= 0)
                previous = label;
        }
        return labels;
    }

    private static string Normalize(string word) =>
        new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant().Replace('ё', 'е');

    /// <summary>Exact matches of an edit-distance alignment (the same procedure as the scorer of the truth kit).</summary>
    private static List<(int Truth, int Hyp)> Align(IReadOnlyList<string> truth, IReadOnlyList<string> hyp)
    {
        int n = truth.Count, m = hyp.Count;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i;
        for (var j = 0; j <= m; j++) d[0, j] = j;
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (truth[i - 1] == hyp[j - 1] ? 0 : 1));

        var pairs = new List<(int, int)>();
        int a = n, b = m;
        while (a > 0 && b > 0)
        {
            if (truth[a - 1] == hyp[b - 1] && d[a, b] == d[a - 1, b - 1]) { pairs.Add((a - 1, b - 1)); a--; b--; }
            else if (d[a, b] == d[a - 1, b - 1] + 1) { a--; b--; }
            else if (d[a, b] == d[a - 1, b] + 1) a--;
            else b--;
        }
        pairs.Reverse();
        return pairs;
    }

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
    }
}
