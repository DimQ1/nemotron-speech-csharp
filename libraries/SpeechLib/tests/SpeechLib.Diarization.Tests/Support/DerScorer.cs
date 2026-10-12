using System.Globalization;

namespace SpeechLib.Diarization.Tests;

/// <summary>Frame-based diarization error rate, the way pyannote.metrics scores it: collar around reference boundaries, overlap scored, optimal one-to-one speaker mapping.</summary>
public static class DerScorer
{
    public sealed record Result(double Der, double Miss, double FalseAlarm, double Confusion, double RefSeconds)
    {
        public override string ToString() =>
            $"DER {Der * 100:F2}% (miss {Miss * 100:F2} fa {FalseAlarm * 100:F2} conf {Confusion * 100:F2}) over {RefSeconds:F1}s";
    }

    public readonly record struct Segment(double Start, double End, string Speaker);

    private const double Step = 0.01;

    /// <summary>Parses an RTTM ("SPEAKER file 1 start dur &lt;NA&gt; &lt;NA&gt; spk ...").</summary>
    public static List<Segment> ReadRttm(string path)
    {
        var list = new List<Segment>();
        foreach (var line in File.ReadLines(path))
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 8 || p[0] != "SPEAKER") continue;
            double s = double.Parse(p[3], CultureInfo.InvariantCulture);
            double d = double.Parse(p[4], CultureInfo.InvariantCulture);
            list.Add(new Segment(s, s + d, p[7]));
        }
        return list;
    }

    public static Segment[] ToSegments(IEnumerable<SpeakerTurn> turns) =>
        turns.Select(t => new Segment(t.Start, t.End, "spk" + t.Speaker)).ToArray();

    /// <param name="collar">Total collar width in seconds, pyannote.metrics semantics: <c>collar / 2</c> is excluded on each side of every reference boundary (NIST's "0.25 s collar" is <c>collar: 0.5</c>).</param>
    public static Result Score(IReadOnlyList<Segment> reference, IReadOnlyList<Segment> hypothesis, double collar = 0.25,
        double? totalSeconds = null)
    {
        double half = collar / 2;
        double end = Math.Max(reference.Count == 0 ? 0 : reference.Max(s => s.End), hypothesis.Count == 0 ? 0 : hypothesis.Max(s => s.End));
        if (totalSeconds is { } ts) end = Math.Max(end, ts);
        int n = (int)Math.Ceiling(end / Step) + 1;

        var refSpk = reference.Select(s => s.Speaker).Distinct().ToList();
        var hypSpk = hypothesis.Select(s => s.Speaker).Distinct().ToList();
        var refMask = Mask(reference, refSpk, n);
        var hypMask = Mask(hypothesis, hypSpk, n);

        // frames excluded by the collar around every reference boundary
        var skip = new bool[n];
        if (collar > 0)
        {
            foreach (var s in reference)
            {
                Mark(skip, s.Start - half, s.Start + half);
                Mark(skip, s.End - half, s.End + half);
            }
        }

        // co-occurrence of reference and hypothesis speakers over scored frames
        var co = new long[refSpk.Count, hypSpk.Count];
        for (int f = 0; f < n; f++)
        {
            if (skip[f]) continue;
            for (int r = 0; r < refSpk.Count; r++)
            {
                if (!refMask[r][f]) continue;
                for (int h = 0; h < hypSpk.Count; h++)
                    if (hypMask[h][f]) co[r, h]++;
            }
        }
        var map = BestMapping(co, refSpk.Count, hypSpk.Count); // map[r] = hyp index or -1

        long totalRef = 0, miss = 0, fa = 0, conf = 0;
        for (int f = 0; f < n; f++)
        {
            if (skip[f]) continue;
            int nr = 0, nh = 0, correct = 0;
            for (int r = 0; r < refSpk.Count; r++)
            {
                if (!refMask[r][f]) continue;
                nr++;
                if (map[r] >= 0 && hypMask[map[r]][f]) correct++;
            }
            for (int h = 0; h < hypSpk.Count; h++) if (hypMask[h][f]) nh++;
            totalRef += nr;
            miss += Math.Max(0, nr - nh);
            fa += Math.Max(0, nh - nr);
            conf += Math.Min(nr, nh) - correct;
        }
        double tot = Math.Max(totalRef, 1);
        return new Result((miss + fa + conf) / tot, miss / tot, fa / tot, conf / tot, totalRef * Step);
    }

    private static bool[][] Mask(IReadOnlyList<Segment> segs, List<string> speakers, int n)
    {
        var m = speakers.Select(_ => new bool[n]).ToArray();
        foreach (var s in segs)
        {
            int k = speakers.IndexOf(s.Speaker);
            int a = Math.Max(0, (int)Math.Round(s.Start / Step));
            int b = Math.Min(n, (int)Math.Round(s.End / Step));
            for (int f = a; f < b; f++) m[k][f] = true;
        }
        return m;
    }

    private static void Mark(bool[] skip, double from, double to)
    {
        int a = Math.Max(0, (int)Math.Round(from / Step));
        int b = Math.Min(skip.Length, (int)Math.Round(to / Step));
        for (int f = a; f < b; f++) skip[f] = true;
    }

    /// <summary>Maximum-overlap one-to-one assignment by DP over subsets of hypothesis speakers (at most 8 matter).</summary>
    private static int[] BestMapping(long[,] co, int nRef, int nHyp)
    {
        var map = new int[nRef];
        Array.Fill(map, -1);
        if (nRef == 0 || nHyp == 0) return map;
        if (nHyp > 20) throw new NotSupportedException("Too many hypothesis speakers for the subset DP.");
        int full = 1 << nHyp;
        var best = new long[nRef + 1, full];
        var choice = new int[nRef + 1, full];
        for (int r = nRef - 1; r >= 0; r--)
        {
            for (int used = 0; used < full; used++)
            {
                long b = best[r + 1, used];
                int c = -1;
                for (int h = 0; h < nHyp; h++)
                {
                    if ((used >> h & 1) != 0) continue;
                    long v = co[r, h] + best[r + 1, used | 1 << h];
                    if (v > b) { b = v; c = h; }
                }
                best[r, used] = b;
                choice[r, used] = c;
            }
        }
        int u = 0;
        for (int r = 0; r < nRef; r++)
        {
            int c = choice[r, u];
            map[r] = c;
            if (c >= 0) u |= 1 << c;
        }
        return map;
    }
}
