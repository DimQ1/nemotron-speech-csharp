// DiarizationEval: runs SpeechLib.Diarization over wavs and reports DER, RTF, peak memory and parity.
//
//   dotnet run -c Release --project tools/DiarizationEval -- --model <folder> --testset <dir with *.wav + *.rttm>
//       [--precision int8|fp32|auto] [--preset offline|low_latency|very_low_latency|ultra_low_latency|ncpp_v3_streaming|eco_2s]
//       [--threads 4] [--files conv00_en_2spk,conv04_en_4spk] [--post raw|default] [--stream-block 1.0]
//       [--compare <DiarBench dump .f32>]   (single file: probability parity against a dump)
//       [--dump <out.f32>]                  (single file: write the 10 ms probabilities)
//
// --stream-block S feeds the audio S seconds at a time through StreamingDiarizer (live simulation) instead of the
// OfflineDiarizer wrapper; both run the same engine.
using System.Diagnostics;
using System.Globalization;
using SpeechLib.Diarization;
using SpeechLib.Diarization.Tests;

var a = Args.Parse(args);
string modelFolder = a.Get("model") ?? throw new ArgumentException("--model required");
var precision = Enum.Parse<DiarizationPrecision>(a.Get("precision", "auto")!, ignoreCase: true);
var preset = ParsePreset(a.Get("preset", "low_latency")!);
int threads = int.Parse(a.Get("threads", "4")!);
var post = a.Get("post", "raw") == "default" ? PostProcessingOptions.Default : PostProcessingOptions.Raw;
double? streamBlock = a.Get("stream-block") is { } sb ? double.Parse(sb, CultureInfo.InvariantCulture) : null;

var sw = Stopwatch.StartNew();
using var model = DiarizationModel.Load(new DiarizationModelOptions { ModelFolder = modelFolder, Precision = precision, Threads = threads });
Console.WriteLine($"model {model.Descriptor.StepFile} ({model.Descriptor.Precision}) ep={model.ExecutionProvider} threads={threads} load={sw.ElapsedMilliseconds} ms ws={Proc.WorkingSetMb():F0} MB");
Console.WriteLine($"preset {preset} geometry={model.Config.GetGeometry(preset)} buffer latency={model.Config.GetGeometry(preset).BufferLatencySeconds:F2} s post={(ReferenceEquals(post, PostProcessingOptions.Raw) ? "raw" : "default")}");

var files = new List<string>();
if (a.Get("wav") is { } single) files.Add(single);
else
{
    string dir = a.Get("testset") ?? throw new ArgumentException("--testset or --wav required");
    var wanted = a.Get("files")?.Split(',');
    files.AddRange(Directory.GetFiles(dir, "*.wav").Where(f => wanted == null || wanted.Contains(Path.GetFileNameWithoutExtension(f))).Order());
}

double totalAudio = 0, totalElapsed = 0, worstRtf = 0;
double sumMiss = 0, sumFa = 0, sumConf = 0, sumRef = 0, sumErr0 = 0, sumRef0 = 0, sumDerMean = 0;
int scored = 0;
foreach (var wav in files)
{
    DiarizationResult result;
    var t0 = Stopwatch.GetTimestamp();
    if (streamBlock is { } blk) result = RunStreaming(model, wav, preset, post, blk);
    else
    {
        using var off = new OfflineDiarizer(model, new OfflineDiarizerOptions { Preset = preset, PostProcessing = post });
        result = off.DiarizeWavFile(wav);
    }
    double elapsed = Stopwatch.GetElapsedTime(t0).TotalSeconds;
    totalAudio += result.DurationSeconds;
    totalElapsed += elapsed;
    worstRtf = Math.Max(worstRtf, elapsed / result.DurationSeconds);

    string name = Path.GetFileNameWithoutExtension(wav);
    var firstSeen = result.Turns.OrderBy(t => t.Start).Select(t => t.Speaker).Distinct().ToList();
    string order = firstSeen.SequenceEqual(firstSeen.Order()) ? "ordered" : "OUT-OF-ORDER[" + string.Join(",", firstSeen) + "]";
    string line = $"{name,-22} {result.DurationSeconds,7:F1}s RTF {elapsed / result.DurationSeconds:F4} speakers {result.SpeakerCount} ({order}) turns {result.Turns.Count}";
    string rttm = Path.ChangeExtension(wav, ".rttm");
    if (File.Exists(rttm))
    {
        var reference = DerScorer.ReadRttm(rttm);
        var hyp = DerScorer.ToSegments(result.Turns);
        var d25 = DerScorer.Score(reference, hyp, 0.25, result.DurationSeconds);
        var d0 = DerScorer.Score(reference, hyp, 0.0, result.DurationSeconds);
        line += $"  DER(c.25) {d25.Der * 100:F2} (m {d25.Miss * 100:F2} fa {d25.FalseAlarm * 100:F2} c {d25.Confusion * 100:F2})  DER(c0) {d0.Der * 100:F2}";
        sumMiss += d25.Miss * d25.RefSeconds; sumFa += d25.FalseAlarm * d25.RefSeconds; sumConf += d25.Confusion * d25.RefSeconds; sumRef += d25.RefSeconds;
        sumErr0 += d0.Der * d0.RefSeconds; sumRef0 += d0.RefSeconds;
        sumDerMean += d25.Der; scored++;
    }
    Console.WriteLine(line);

    if (a.Get("compare") is { } dumpPath && result.Timeline != null)
        Compare(result.Timeline, dumpPath);
    if (a.Get("dump") is { } outPath && result.Timeline != null)
    {
        File.WriteAllBytes(outPath, System.Runtime.InteropServices.MemoryMarshal.AsBytes(result.Timeline.ToMatrix().AsSpan()).ToArray());
        Console.WriteLine($"dumped {result.Timeline.FrameCount} x 8 -> {outPath}");
    }
}

if (sumRef > 0)
{
    Console.WriteLine($"TOTAL weighted by reference seconds: DER(c.25) {(sumMiss + sumFa + sumConf) / sumRef * 100:F2} (m {sumMiss / sumRef * 100:F2} fa {sumFa / sumRef * 100:F2} c {sumConf / sumRef * 100:F2})  DER(c0) {sumErr0 / sumRef0 * 100:F2}");
    Console.WriteLine($"MEAN over {scored} files (the spike README averages files): DER(c.25) {sumDerMean / scored * 100:F2}");
}
Console.WriteLine($"audio {totalAudio:F1}s in {totalElapsed:F1}s -> RTF {totalElapsed / Math.Max(totalAudio, 1e-9):F4} (worst file {worstRtf:F4})  peak ws {Proc.PeakWorkingSetMb():F0} MB  managed {GC.GetTotalMemory(false) / 1e6:F1} MB");
return 0;

static DiarizationResult RunStreaming(DiarizationModel model, string wav, DiarizationPreset preset, PostProcessingOptions post, double block)
{
    var sw = Stopwatch.StartNew();
    var audio = ReadWav(wav);
    int n = Math.Max(160, (int)(block * 16000));
    using var d = new StreamingDiarizer(model, new StreamingDiarizerOptions { Preset = preset, PostProcessing = post });
    var perSecond = new List<double>();
    for (int i = 0; i < audio.Length; i += n)
    {
        int len = Math.Min(n, audio.Length - i);
        var t = Stopwatch.GetTimestamp();
        d.Feed(audio.AsSpan(i, len));
        perSecond.Add(Stopwatch.GetElapsedTime(t).TotalSeconds / (len / 16000.0));
    }
    d.Flush();
    perSecond.Sort();
    Console.WriteLine($"  feed cost per audio-second: p50 {perSecond[perSecond.Count / 2]:F3} p95 {perSecond[(int)(perSecond.Count * 0.95)]:F3} max {perSecond[^1]:F3}");
    return new DiarizationResult(d.GetTurns(), d.Timeline, audio.Length / 16000.0, sw.Elapsed.TotalSeconds, preset);
}

static float[] ReadWav(string path)
{
    var b = File.ReadAllBytes(path);
    int pos = 12;
    while (pos + 8 <= b.Length)
    {
        string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
        int size = BitConverter.ToInt32(b, pos + 4);
        if (id == "data")
        {
            int n = size / 2;
            var x = new float[n];
            for (int i = 0; i < n; i++) x[i] = BitConverter.ToInt16(b, pos + 8 + i * 2) / 32768f;
            return x;
        }
        pos += 8 + size + (size & 1);
    }
    throw new InvalidDataException("no data chunk");
}

static void Compare(SpeakerActivityTimeline timeline, string dumpPath)
{
    var raw = File.ReadAllBytes(dumpPath);
    var dump = new float[raw.Length / 4];
    Buffer.BlockCopy(raw, 0, dump, 0, raw.Length);
    var ours = timeline.ToMatrix();
    int rows = Math.Min(ours.Length, dump.Length) / 8;
    double max = 0, sum = 0;
    long flips = 0;
    for (int i = 0; i < rows * 8; i++)
    {
        double d = Math.Abs(ours[i] - dump[i]);
        max = Math.Max(max, d);
        sum += d;
        if ((ours[i] >= 0.5f) != (dump[i] >= 0.5f)) flips++;
    }
    Console.WriteLine($"  parity vs {Path.GetFileName(dumpPath)}: rows ours {ours.Length / 8} dump {dump.Length / 8}, compared {rows}, max|dp| {max:E2} mean {sum / (rows * 8):E2} flipped decisions {flips}");
}

static DiarizationPreset ParsePreset(string s) => s.ToLowerInvariant() switch
{
    "offline" => DiarizationPreset.Offline,
    "low_latency" => DiarizationPreset.LowLatency,
    "very_low_latency" => DiarizationPreset.VeryLowLatency,
    "ultra_low_latency" => DiarizationPreset.UltraLowLatency,
    "ncpp_v3_streaming" => DiarizationPreset.NcppV3Streaming,
    "eco_2s" => DiarizationPreset.Eco2s,
    _ => throw new ArgumentException($"unknown preset {s}"),
};

static class Proc
{
    public static double WorkingSetMb() => Process.GetCurrentProcess().WorkingSet64 / 1e6;
    public static double PeakWorkingSetMb() => Process.GetCurrentProcess().PeakWorkingSet64 / 1e6;
}

sealed class Args
{
    private readonly Dictionary<string, string> _d = new();

    public static Args Parse(string[] a)
    {
        var r = new Args();
        for (int i = 0; i < a.Length; i++)
            if (a[i].StartsWith("--")) r._d[a[i][2..]] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
        return r;
    }

    public string? Get(string k, string? def = null) => _d.TryGetValue(k, out var v) ? v : def;
}
