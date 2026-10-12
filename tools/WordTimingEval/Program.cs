// Accuracy of Parakeet decoder word timings.
//
// Usage:
//   WordTimingEval <parakeetModelDir> <audioRoot> --truth [--clips 18] [--group 6] [--chunk 0.32] [--seed 1]
//   WordTimingEval <parakeetModelDir> <audioRoot> [--clips 60] [--group 6] [--chunk 0.32] [--seed 1]
//
// --truth: <audioRoot> holds synthesized clips NNNN.wav + NNNN.txt + NNNN.times ("start<TAB>word" per
//   word; SAPI SpeakProgress word starts). Clips are concatenated in groups with random 1.0-2.5 s gaps of
//   room tone and every decoder word start is compared with the synthesizer's start of the same word
//   (word alignment by edit distance). The reference is independent of the recognizer.
// Without --truth: Common Voice style clips; the reference is each clip's energy-VAD speech onset /
//   offset (a rough one: breaths and clicks around the speech), compared with the first / last decoder word.
//
// Modes: streaming at --chunk and at 1.0 s through OfflineTranscriber (decoder times and, for comparison,
// the old estimated times: UseDecoderTimings = false) and the whole-clip offline decode (TranscribeWords).
using System.Globalization;
using SpeechLib;
using SpeechLib.Models;
using SpeechLib.ParakeetTdt;
using VoiceType.Uno.History;

if (args.Length < 2)
{
    Console.WriteLine("Usage: WordTimingEval <parakeetModelDir> <audioRoot> [--truth] [--clips N] [--group 6] [--chunk 0.32] [--seed 1]");
    return 1;
}

string modelDir = args[0], root = Path.GetFullPath(args[1]);
int clipCount = 60, group = 6, seed = 1;
double chunk = ParakeetTdtRecognizer.DefaultChunkSeconds;
bool truth = false;
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--clips": clipCount = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--group": group = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--chunk": chunk = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--seed": seed = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--truth": truth = true; break;
    }
}

return Eval.Run(modelDir, root, truth, clipCount, group, chunk, seed);

static class Eval
{
    public static int Run(string modelDir, string root, bool truth, int clipCount, int group, double chunk, int seed)
    {
        var rng = new Random(seed);
        if (File.Exists(root))
            return RunFile(modelDir, root, chunk);
        var modes =new[] { ($"stream {chunk:0.00}s", chunk), ("stream 1.00s", 1.0) };

        if (truth)
        {
            var clips = Directory.EnumerateFiles(root, "*.wav").OrderBy(p => p, StringComparer.Ordinal).Take(clipCount)
                .Select(p => (Audio: ReadWav(p), Truth: File.ReadAllLines(Path.ChangeExtension(p, ".times"))
                    .Select(l => l.Split('\t')).Select(a => (Word: a[1], Start: double.Parse(a[0], CultureInfo.InvariantCulture))).ToList()))
                .ToList();
            var groups = new List<(float[] Audio, List<(string Word, double Start)> Truth)>();
            for (int g = 0; g < clips.Count; g += group)
            {
                var audio = new List<float>();
                var words = new List<(string Word, double Start)>();
                AppendRoomTone(audio, rng, 1.0 + rng.NextDouble());
                for (int k = g; k < Math.Min(clips.Count, g + group); k++)
                {
                    double offset = (double)audio.Count / 16000;
                    words.AddRange(clips[k].Truth.Select(t => (t.Word, t.Start + offset)));
                    audio.AddRange(clips[k].Audio);
                    AppendRoomTone(audio, rng, 1.0 + rng.NextDouble() * 1.5);
                }
                groups.Add((audio.ToArray(), words));
            }
            Console.WriteLine($"{clips.Count} TTS clips, {groups.Count} concatenations of up to {group}");
            foreach (var (name, chunkSeconds) in modes)
            {
                using var recognizer = new ParakeetTdtRecognizer(modelDir, chunkSeconds: chunkSeconds, refineTimings: Environment.GetEnvironmentVariable("WTE_RAW") != "1");
                var dec = new TruthStats { Label = name + " dec" };
                var est = new TruthStats { Label = name + " est" };
                foreach (var (audio, words) in groups)
                {
                    dec.Add(words, Words(OfflineTranscriber.Run(recognizer, audio, new OfflineTranscriptionOptions { UseDecoderTimings = true }).Document));
                    est.Add(words, Words(OfflineTranscriber.Run(recognizer, audio, new OfflineTranscriptionOptions { UseDecoderTimings = false }).Document));
                }
                dec.Print(name + " decoder");
                est.Print(name + " ESTIMATED (old)");
            }
            using (var recognizer = new ParakeetTdtRecognizer(modelDir, refineTimings: Environment.GetEnvironmentVariable("WTE_RAW") != "1"))
            {
                var dec = new TruthStats { Label = "offline" };
                foreach (var clip in clips)
                    dec.Add(clip.Truth, recognizer.TranscribeWords(clip.Audio).Select(w => (w.Word, w.StartSeconds, w.EndSeconds)).ToList());
                dec.Print("offline whole-clip decoder");
            }
            return 0;
        }

        var cv = Directory.EnumerateFiles(root, "*.wav", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (Path: p, Audio: ReadWav(p)))
            .Where(c => c.Audio.Length is > 24000 and < 16000 * 14)
            .Take(clipCount)
            .ToList();
        Console.WriteLine($"{cv.Count} clips from {root}, groups of {group}, chunk {chunk:0.00}s");
        var refs = cv.Select(c => RunVad(c.Audio)).ToList();

        var cvGroups = new List<(float[] Audio, List<Interval> Clips)>();
        for (int g = 0; g < cv.Count; g += group)
        {
            var audio = new List<float>();
            var intervals = new List<Interval>();
            AppendRoomTone(audio, rng, 1.0 + rng.NextDouble());
            for (int k = g; k < Math.Min(cv.Count, g + group); k++)
            {
                double offset = (double)audio.Count / 16000;
                var r = refs[k];
                if (r.Valid)
                    intervals.Add(new Interval(offset + r.Onset, offset + r.Offset, r.Pauses.Select(p => (offset + p.From, offset + p.To)).ToList()));
                audio.AddRange(cv[k].Audio);
                AppendRoomTone(audio, rng, 1.0 + rng.NextDouble() * 1.5);
            }
            cvGroups.Add((audio.ToArray(), intervals));
        }

        foreach (var (name, chunkSeconds) in modes)
        {
            using var recognizer = new ParakeetTdtRecognizer(modelDir, chunkSeconds: chunkSeconds, refineTimings: Environment.GetEnvironmentVariable("WTE_RAW") != "1");
            var decoder = new Collected();
            var estimated = new Collected();
            foreach (var (audio, intervals) in cvGroups)
            {
                var withDecoder = OfflineTranscriber.Run(recognizer, audio, new OfflineTranscriptionOptions { UseDecoderTimings = true });
                var without = OfflineTranscriber.Run(recognizer, audio, new OfflineTranscriptionOptions { UseDecoderTimings = false });
                decoder.Add(intervals, Words(withDecoder.Document));
                estimated.Add(intervals, Words(without.Document));
                decoder.Kinds.AddRange(withDecoder.Document.Segments.Select(s => s.WordTiming));
            }
            decoder.Print(name + " decoder");
            Console.WriteLine($"  segments marked decoder: {decoder.Kinds.Count(k => k == WordTimingKinds.Decoder)}/{decoder.Kinds.Count}");
            estimated.Print(name + " ESTIMATED (old)");
        }

        using (var recognizer = new ParakeetTdtRecognizer(modelDir, refineTimings: Environment.GetEnvironmentVariable("WTE_RAW") != "1"))
        {
            var offline = new Collected();
            for (int k = 0; k < cv.Count; k++)
            {
                if (!refs[k].Valid) continue;
                var words = recognizer.TranscribeWords(cv[k].Audio).Select(w => (w.Word, w.StartSeconds, w.EndSeconds)).ToList();
                offline.Add([new Interval(refs[k].Onset, refs[k].Offset, refs[k].Pauses.Select(p => (p.From, p.To)).ToList())], words);
            }
            offline.Print("offline whole-clip decoder");
        }
        return 0;
    }

    // A single long recording: segment / word sanity (order, overlaps) and how many words span a clear
    // pause (>= 300 ms of unvoiced audio between 10 ms voiced frames), decoder vs estimated times.
    static int RunFile(string modelDir, string wav, double chunk)
    {
        var audio = ReadWav(wav);
        var (_, runs) = (0, VoicedRuns(audio));
        var pauses = new List<(double From, double To)>();
        for (int i = 1; i < runs.Count; i++)
            if (runs[i].From - runs[i - 1].To >= 0.3) pauses.Add((runs[i - 1].To, runs[i].From));
        Console.WriteLine($"{Path.GetFileName(wav)}: {audio.Length / 16000.0:F1}s, {pauses.Count} pauses >= 300 ms");
        using var recognizer = new ParakeetTdtRecognizer(modelDir, chunkSeconds: chunk, refineTimings: Environment.GetEnvironmentVariable("WTE_RAW") != "1");
        foreach (var useDecoder in new[] { true, false })
        {
            var doc = OfflineTranscriber.Run(recognizer, audio, new OfflineTranscriptionOptions { UseDecoderTimings = useDecoder }).Document;
            var words = doc.Segments.SelectMany(s => s.Words).ToList();
            int overlaps = 0, backwards = 0, inPause = 0;
            for (int i = 1; i < doc.Segments.Count; i++)
                if (doc.Segments[i].Start < doc.Segments[i - 1].End - 1e-6) overlaps++;
            for (int i = 1; i < words.Count; i++)
                if (words[i].Start < words[i - 1].Start - 1e-6) backwards++;
            foreach (var (from, to) in pauses)
            {
                double mid = (from + to) / 2;
                if (words.Any(w => w.Start < mid - 0.05 && w.End > mid + 0.05)) inPause++;
            }
            Console.WriteLine($"== {(useDecoder ? "decoder" : "ESTIMATED (old)")} chunk {chunk:0.00}s: {doc.Segments.Count} segments ({doc.Segments.Count(s => s.WordTiming == "decoder")} decoder), {words.Count} words, segment overlaps {overlaps}, word order violations {backwards}, pauses inside a word {inPause}/{pauses.Count}");
            if (Environment.GetEnvironmentVariable("WTE_SEGMENTS") == "1")
                foreach (var s in doc.Segments)
                    Console.WriteLine($"   {s.Start:F2}-{s.End:F2} [{s.WordTiming}] {s.Text[..Math.Min(70, s.Text.Length)]}");
        }
        return 0;
    }

    static List<(double From, double To)> VoicedRuns(float[] audio)
    {
        const int Frame = 160;
        int frames = audio.Length / Frame;
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int k = 0; k < Frame; k++) { double x = audio[f * Frame + k]; sum += x * x; }
            db[f] = 10 * Math.Log10(sum / Frame + 1e-12);
        }
        var sorted = (double[])db.Clone();
        Array.Sort(sorted);
        double thr = Math.Max(sorted[(int)(frames * 0.15)] + 12, -60);
        var runs = new List<(double, double)>();
        for (int f = 0; f < frames;)
        {
            if (db[f] <= thr) { f++; continue; }
            int g = f;
            while (g < frames && db[g] > thr) g++;
            if (g - f >= 3) runs.Add((f * 0.01, g * 0.01));
            f = g;
        }
        return runs;
    }

    static List<(string Word, double Start, double End)> Words(TranscriptDocument doc) =>
        doc.Segments.SelectMany(s => s.Words).Select(w => (w.Text, w.Start, w.End)).ToList();

    public static double Median(IEnumerable<double> v) => Percentile(v, 0.5);

    public static double Percentile(IEnumerable<double> values, double p)
    {
        var a = values.OrderBy(x => x).ToArray();
        if (a.Length == 0) return double.NaN;
        return a[Math.Min(a.Length - 1, (int)(p * a.Length))];
    }

    public static string Norm(string w) => new string(w.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    public static List<(int T, int H)> AlignWords(IReadOnlyList<string> t, IReadOnlyList<string> h)
    {
        int n = t.Count, m = h.Count;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
            for (int j = 1; j <= m; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (t[i - 1] == h[j - 1] ? 0 : 1));
        var pairs = new List<(int, int)>();
        for (int i = n, j = m; i > 0 && j > 0;)
        {
            if (t[i - 1] == h[j - 1] && d[i, j] == d[i - 1, j - 1]) { pairs.Add((i - 1, j - 1)); i--; j--; }
            else if (d[i, j] == d[i - 1, j - 1] + 1) { i--; j--; }
            else if (d[i, j] == d[i - 1, j] + 1) i--;
            else j--;
        }
        pairs.Reverse();
        return pairs;
    }

    static void AppendRoomTone(List<float> audio, Random rng, double seconds)
    {
        int n = (int)(seconds * 16000);
        for (int i = 0; i < n; i++)
            audio.Add((float)((rng.NextDouble() * 2 - 1) * 0.0004));
    }

    static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            int size = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "data")
            {
                int n = Math.Min(size, bytes.Length - pos - 8) / 2;
                var s = new float[n];
                for (int i = 0; i < n; i++)
                    s[i] = BitConverter.ToInt16(bytes, pos + 8 + i * 2) / 32768f;
                return s;
            }
            pos += 8 + size + (size & 1);
        }
        throw new InvalidDataException(path);
    }

    // Energy VAD: 10 ms frames, voiced above the 15th-percentile level + 12 dB (floor -60 dB). Speech islands
    // shorter than 250 ms (breaths, clicks) are dropped; gaps shorter than 250 ms are bridged.
    static Vad RunVad(float[] audio)
    {
        const int Frame = 160;
        int frames = audio.Length / Frame;
        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double sum = 0;
            for (int k = 0; k < Frame; k++) { double s = audio[f * Frame + k]; sum += s * s; }
            db[f] = 10 * Math.Log10(sum / Frame + 1e-12);
        }
        var sorted = (double[])db.Clone();
        Array.Sort(sorted);
        double noise = sorted[(int)(frames * 0.15)], loud = sorted[(int)(frames * 0.95)];
        if (loud - noise < 12) return new Vad(false, 0, 0, []);
        double thr = Math.Max(noise + 12, -60);
        var runs = new List<(int From, int To)>();
        for (int f = 0; f < frames;)
        {
            if (db[f] <= thr) { f++; continue; }
            int g = f;
            while (g < frames && db[g] > thr) g++;
            runs.Add((f, g));
            f = g;
        }
        var islands = new List<(int From, int To)>();
        foreach (var r in runs)
        {
            if (islands.Count > 0 && r.From - islands[^1].To < 25)
                islands[^1] = (islands[^1].From, r.To);
            else
                islands.Add(r);
        }
        islands = islands.Where(i => i.To - i.From >= 25).ToList();
        if (islands.Count == 0) return new Vad(false, 0, 0, []);
        var pauses = new List<(double, double)>();
        int first = islands[0].From, last = islands[^1].To;
        var inside = runs.Where(r => r.From >= first && r.To <= last).ToList();
        for (int i = 1; i < inside.Count; i++)
            if (inside[i].From - inside[i - 1].To >= 20)
                pauses.Add((inside[i - 1].To * 0.01, inside[i].From * 0.01));
        return new Vad(true, first * 0.01, last * 0.01, pauses);
    }

    public static string Fmt(double seconds) => double.IsNaN(seconds) ? "n/a" : (seconds * 1000).ToString("F0", CultureInfo.InvariantCulture);
}

record Vad(bool Valid, double Onset, double Offset, List<(double From, double To)> Pauses);

record Interval(double Onset, double Offset, List<(double From, double To)> Pauses);

sealed class Collected
{
    public List<double> StartErr = [], EndErr = [];
    public List<string> Kinds = [];
    public int Pauses, PauseViolations, WordCount, NonMonotonic, BadLength, Unmatched;

    public void Add(List<Interval> intervals, List<(string Word, double Start, double End)> words)
    {
        WordCount += words.Count;
        for (int i = 0; i < words.Count; i++)
        {
            if (words[i].End < words[i].Start - 1e-6) BadLength++;
            if (i > 0 && words[i].Start < words[i - 1].Start - 1e-6) NonMonotonic++;
        }
        foreach (var clip in intervals)
        {
            var inside = words.Where(w => (w.Start + w.End) / 2 >= clip.Onset - 0.5 && (w.Start + w.End) / 2 <= clip.Offset + 0.5).ToList();
            if (inside.Count == 0) { Unmatched++; continue; }
            StartErr.Add(inside[0].Start - clip.Onset);
            EndErr.Add(inside[^1].End - clip.Offset);
            foreach (var (from, to) in clip.Pauses)
            {
                Pauses++;
                double mid = (from + to) / 2;
                if (inside.Any(w => w.Start < mid - 0.05 && w.End > mid + 0.05))
                    PauseViolations++;
            }
        }
    }

    public void Print(string name)
    {
        Console.WriteLine($"== {name} ==");
        Console.WriteLine($"  clips matched {StartErr.Count} (unmatched {Unmatched})  first-word start error ms: median|e| {Eval.Fmt(Eval.Median(StartErr.Select(Math.Abs)))}  p95|e| {Eval.Fmt(Eval.Percentile(StartErr.Select(Math.Abs), 0.95))}  signed median {Eval.Fmt(Eval.Median(StartErr))}");
        Console.WriteLine($"                 last-word end error ms: median|e| {Eval.Fmt(Eval.Median(EndErr.Select(Math.Abs)))}  p95|e| {Eval.Fmt(Eval.Percentile(EndErr.Select(Math.Abs), 0.95))}  signed median {Eval.Fmt(Eval.Median(EndErr))}");
        Console.WriteLine($"  interior pauses inside a word: {PauseViolations}/{Pauses}  words {WordCount}  non-monotonic {NonMonotonic}  zero/neg-length {BadLength}");
    }
}

sealed class TruthStats
{
    public string Label = "";
    public List<double> StartErr = [], EndErr = [];
    public int TruthWords, Matched, NonMonotonic;

    public void Add(IReadOnlyList<(string Word, double Start)> truth, List<(string Word, double Start, double End)> words)
    {
        TruthWords += truth.Count;
        for (int i = 1; i < words.Count; i++)
            if (words[i].Start < words[i - 1].Start - 1e-6) NonMonotonic++;
        var pairs = Eval.AlignWords(truth.Select(x => Eval.Norm(x.Word)).ToList(), words.Select(x => Eval.Norm(x.Word)).ToList());
        foreach (var (t, h) in pairs)
        {
            Matched++;
            StartErr.Add(words[h].Start - truth[t].Start);
            if (Math.Abs(words[h].Start - truth[t].Start) > 0.2 && Environment.GetEnvironmentVariable("WTE_WORST") == "1")
                Console.WriteLine($"    worst[{Label}]: '{truth[t].Word}' truth {truth[t].Start:F2} got {words[h].Start:F2}-{words[h].End:F2} (err {(words[h].Start - truth[t].Start) * 1000:F0}) prev truth '{(t > 0 ? truth[t - 1].Word : "")}' {(t > 0 ? truth[t - 1].Start : 0):F2}, gap to prev {(t > 0 ? truth[t].Start - truth[t - 1].Start : 0):F2}");
            // Words close together: the end of a word should meet the start of the next one.
            if (t + 1 < truth.Count && truth[t + 1].Start - truth[t].Start < 0.6)
                EndErr.Add(words[h].End - truth[t + 1].Start);
        }
    }

    public void Print(string name)
    {
        Console.WriteLine($"== {name} ==  words {Matched}/{TruthWords} matched, non-monotonic {NonMonotonic}");
        Console.WriteLine($"  start error ms: median|e| {Eval.Fmt(Eval.Median(StartErr.Select(Math.Abs)))}  p95|e| {Eval.Fmt(Eval.Percentile(StartErr.Select(Math.Abs), 0.95))}  signed median {Eval.Fmt(Eval.Median(StartErr))}  p5 {Eval.Fmt(Eval.Percentile(StartErr, 0.05))}  p95 {Eval.Fmt(Eval.Percentile(StartErr, 0.95))}");
        Console.WriteLine($"  end-vs-next-start ms (n={EndErr.Count}): median|e| {Eval.Fmt(Eval.Median(EndErr.Select(Math.Abs)))}  p95|e| {Eval.Fmt(Eval.Percentile(EndErr.Select(Math.Abs), 0.95))}  signed median {Eval.Fmt(Eval.Median(EndErr))}");
    }
}
