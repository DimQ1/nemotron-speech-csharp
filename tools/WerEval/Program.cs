// WER evaluation harness for SpeechLib recognizers.
//
// Scans an audio root recursively for {name}.wav + {name}.txt pairs (16 kHz
// mono PCM16, reference text in the sibling .txt). New audio can be added by
// simply dropping a .wav/.txt pair (or a whole folder) under the root — the
// next run picks it up automatically.
//
// Reports, per dataset folder and overall: file count, WER, processing speed
// (RTF and x real-time), and the most frequent model errors (substitutions,
// insertions, deletions) computed from word-level alignment.
//
// Usage:
//   WerEval <parakeet|nemotron|qwen3|qwen3-streaming> <modelDir> <audioRoot> [--max N] [--lang en|ru]
//           [--streaming] [--top N] [--verbose] [--trace] [--save report.md]
//           [--codec wav|opus:<kbps>[:audio|voip[:complexity]]] [--codec-only]
//
// --codec round-trips every utterance through SpeechLib.AudioStorage (the recordings
// history writer + reader) before recognition, to measure what storing audio costs in
// re-recognition accuracy. It also reports encode/decode speed and size per hour.
// --codec-only measures speed/size without loading a model (pass any modelDir).
//
// Nemotron stream options (NemotronRecognizerOptions): --vad (the native silence drop, as the apps run it),
// --threads N (CPU threads), --pause keep|fresh|dual with --pause-sec / --decision-sec / --preroll-sec
// (the pause-state policy), --closing N (zero chunks after the padded flush remainder).
// --hyp-out file.tsv writes "relative path<TAB>hypothesis" per file; --pause-log file.tsv writes
// one line per pause-policy decision (nemotron).
//
// Build (CPU):
//   dotnet build tools/WerEval/WerEval.csproj -c Release
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SpeechLib;
using SpeechLib.AudioStorage;
using SpeechLib.Nemotron;
using SpeechLib.ParakeetTdt;
using SpeechLib.Qwen3;

if (args.Length == 0 || args[0] is "--list-gpus")
{
    if (args.Length == 0)
    {
        Console.WriteLine("Usage: WerEval <parakeet|nemotron|qwen3|qwen3-streaming> <modelDir> <audioRoot> [--max N] [--lang en|ru] [--streaming] [--top N] [--verbose] [--trace] [--save report.md] [--ep cpu|webgpu[...]]");
        Console.WriteLine("       WerEval --list-gpus");
        Console.WriteLine("  --ep specs: cpu (default) | webgpu | webgpu:<hp|lp|index> | webgpu:0,layout=NHWC,capture=1");
        Console.WriteLine("              nemotron: WebGPU runs the encoder on a discrete GPU only; index/options are ignored, lp and no discrete GPU mean CPU");
        Console.WriteLine("  --codec wav|opus:<kbps>[:audio|voip[:complexity]]  round-trip audio through the recording store first; --codec-only skips ASR");
        Console.WriteLine("  parakeet streaming: --chunk <s> --right <s> --left <s> --silence <s> --onset <s> --no-preview (see ParakeetTdtRecognizer)");
        return 1;
    }

    if (!WebGpuExecutionProvider.TryRegister(out var gpuError))
    {
        Console.WriteLine($"WebGPU unavailable: {gpuError}");
        return 4;
    }

    Console.WriteLine("WebGPU adapters:");
    foreach (var gpu in WebGpuExecutionProvider.GetDevices())
        Console.WriteLine($"  {WebGpuExecutionProvider.Describe(gpu)}");
    return 0;
}

var provider = args[0];
var modelDir = args[1];
var audioRoot = Path.GetFullPath(args[2]);

int max = int.MaxValue;
string? lang = null;
bool streaming = false;
bool verbose = false;
bool trace = false;
int topN = 10;
string? savePath = null;
string ep = "cpu";
RecordingAudioFormat? codec = null;
bool codecOnly = false;
var streamingOptions = new ParakeetStreamingOptions();
var nemotron = new NemotronStreamOptions();
string? hypOut = null;
string? pauseLog = null;

for (int i = 3; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--max" when i + 1 < args.Length: max = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--lang" when i + 1 < args.Length: lang = args[++i]; break;
        case "--ep" when i + 1 < args.Length: ep = args[++i]; break;
        case "--streaming": streaming = true; break;
        case "--chunk" when i + 1 < args.Length: streamingOptions.ChunkSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--right" when i + 1 < args.Length: streamingOptions.RightContextSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--left" when i + 1 < args.Length: streamingOptions.LeftContextSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--silence" when i + 1 < args.Length: streamingOptions.SilenceContextSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--onset" when i + 1 < args.Length: streamingOptions.OnsetContextSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--no-preview": streamingOptions.Preview = false; break;
        case "--top" when i + 1 < args.Length: topN = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--verbose": verbose = true; break;
        case "--trace": trace = true; break;
        case "--save" when i + 1 < args.Length: savePath = args[++i]; break;
        case "--codec" when i + 1 < args.Length: codec = RecordingAudioFormat.Parse(args[++i]); break;
        case "--codec-only": codecOnly = true; break;
        case "--vad": nemotron.UseVad = true; break;
        case "--threads" when i + 1 < args.Length: nemotron.Threads = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--pause" when i + 1 < args.Length:
            nemotron.Pause = nemotron.Pause with
            {
                Policy = args[++i] switch
                {
                    "keep" => NemotronPauseState.Keep,
                    "fresh" => NemotronPauseState.Fresh,
                    "dual" => NemotronPauseState.DualBranch,
                    var other => throw new ArgumentException($"Unknown --pause '{other}'. Use keep, fresh or dual."),
                },
            };
            break;
        case "--pause-sec" when i + 1 < args.Length: nemotron.Pause = nemotron.Pause with { PauseSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
        case "--decision-sec" when i + 1 < args.Length: nemotron.Pause = nemotron.Pause with { DecisionSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
        case "--preroll-sec" when i + 1 < args.Length: nemotron.Pause = nemotron.Pause with { PrerollSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture) }; break;
        case "--closing" when i + 1 < args.Length: nemotron.ClosingChunks = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
        case "--hyp-out" when i + 1 < args.Length: hypOut = args[++i]; break;
        case "--pause-log" when i + 1 < args.Length: pauseLog = args[++i]; break;
    }
}

if (!Directory.Exists(audioRoot))
{
    Console.WriteLine($"Audio root not found: {audioRoot}");
    return 2;
}

var codecStats = new CodecStats();
var codecTemp = Path.Combine(Path.GetTempPath(), "wereval-codec-" + Environment.ProcessId);
if (codec is not null)
    Directory.CreateDirectory(codecTemp);

if (codecOnly)
{
    if (codec is null) { Console.WriteLine("--codec-only needs --codec"); return 1; }
    var all = Directory.EnumerateFiles(audioRoot, "*.wav", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal).Take(max).ToList();
    foreach (var wav in all)
        RoundTrip(ReadWav16kMono(wav), codec, codecTemp, codecStats);
    PrintCodecStats(codec, codecStats);
    Directory.Delete(codecTemp, recursive: true);
    return 0;
}

using var recognizer = CreateRecognizer(provider, modelDir, lang, ep, streamingOptions, nemotron);
var currentFile = "";
StreamWriter? pauseWriter = null;
if (recognizer is NemotronUtteranceRecognizer nemotronInfo)
{
    Console.WriteLine($"Nemotron execution provider: {nemotronInfo.ExecutionProvider.Effective} ({nemotronInfo.ExecutionProvider.Summary})");
    Console.WriteLine($"Nemotron stream: vad={nemotron.UseVad} threads={nemotron.Threads?.ToString(CultureInfo.InvariantCulture) ?? "auto"} pause={nemotron.Pause.Policy} " +
        $"(pause {nemotron.Pause.PauseSeconds}s, decision {nemotron.Pause.DecisionSeconds}s, preroll {nemotron.Pause.PrerollSeconds}s) closing={nemotron.ClosingChunks}");
    if (pauseLog is not null)
    {
        pauseWriter = new StreamWriter(pauseLog, append: false, new UTF8Encoding(false));
        pauseWriter.WriteLine("file\tfirstChunk\tchunks\tsilentChunks\tkeepLetters\tfreshLetters\tfreshWon\tkeepPerChunk\tfreshPerChunk\tkeepText\tfreshText");
        nemotronInfo.PauseDecided += d => pauseWriter.WriteLine(
            $"{currentFile}\t{d.FirstChunk}\t{d.Chunks}\t{d.SilentChunks}\t{d.KeepLetters}\t{d.FreshLetters}\t{d.FreshWon}\t" +
            $"{string.Join(',', d.KeepLettersPerChunk)}\t{string.Join(',', d.FreshLettersPerChunk)}\t{d.KeepText.Trim()}\t{d.FreshText.Trim()}");
    }
}
StreamWriter? hypWriter = hypOut is null ? null : new StreamWriter(hypOut, append: false, new UTF8Encoding(false));
if (recognizer is ParakeetTdtRecognizer parakeetInfo && streaming)
    Console.WriteLine($"Parakeet streaming: chunk={parakeetInfo.ChunkSeconds:F2}s right={parakeetInfo.RightContextSeconds:F2}s left={parakeetInfo.LeftContextSeconds:F2}s silence={parakeetInfo.SilenceContextSeconds:F2}s onset={parakeetInfo.OnsetContextSeconds:F2}s preview={parakeetInfo.PreviewEnabled}");

var wavs = Directory.EnumerateFiles(audioRoot, "*.wav", SearchOption.AllDirectories)
    .Where(w => File.Exists(Path.ChangeExtension(w, ".txt")))
    .OrderBy(p => p, StringComparer.Ordinal)
    .Take(max)
    .ToList();

if (wavs.Count == 0)
{
    Console.WriteLine($"No .wav/.txt pairs found under {audioRoot}");
    return 3;
}

var datasets = new Dictionary<string, DatasetAgg>(StringComparer.Ordinal);
var errors = new ErrorCounters();

foreach (var wav in wavs)
{
    var txtPath = Path.ChangeExtension(wav, ".txt");
    var dataset = Path.GetRelativePath(audioRoot, Path.GetDirectoryName(wav) ?? ".")
        .Replace(Path.DirectorySeparatorChar, '/');
    if (dataset == ".") dataset = "(root)";

    var reference = File.ReadAllText(txtPath).Trim();
    var samples = ReadWav16kMono(wav);
    if (codec is not null)
        samples = RoundTrip(samples, codec, codecTemp, codecStats);

    // Infer language from the folder name (e.g. cv17/ru) when not forced.
    if (lang is null && recognizer is ILanguageConfigurable languageConfigurable)
    {
        var inferred = LanguageFromFolder(Path.GetDirectoryName(wav)!);
        if (inferred is not null)
            languageConfigurable.TrySetLanguage(LanguageMapper.Resolve(inferred)!);
    }

    currentFile = Path.GetRelativePath(audioRoot, wav).Replace(Path.DirectorySeparatorChar, '/');
    var sw = Stopwatch.StartNew();
    var run = Transcribe(recognizer, samples, streaming, trace, Path.GetFileName(wav));
    sw.Stop();
    var hypothesis = run.Hypothesis;
    hypWriter?.WriteLine($"{currentFile}\t{hypothesis.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')}");

    var refWords = Tokenize(reference);
    var hypWords = Tokenize(hypothesis);
    int dist = Levenshtein(refWords, hypWords);
    CollectErrors(errors, Align(refWords, hypWords));

    double audioSec = (double)samples.Length / recognizer.SampleRate;
    double computeSec = sw.Elapsed.TotalSeconds;

    if (!datasets.TryGetValue(dataset, out var agg))
    {
        agg = new DatasetAgg(dataset);
        datasets[dataset] = agg;
    }
    agg.Files++;
    agg.RefWords += refWords.Length;
    agg.Errors += dist;
    agg.AudioSeconds += audioSec;
    agg.ComputeSeconds += computeSec;

    if (streaming)
    {
        // How close the live display (committed text + last revisable preview) was to
        // the final transcript, and how early text first appeared (seconds of audio).
        if (run.PreviewHypothesis is not null)
        {
            agg.PreviewWords += hypWords.Length;
            agg.PreviewErrors += Levenshtein(hypWords, Tokenize(run.PreviewHypothesis));
        }
        if (run.FirstCommitAt is { } commitAt)
        {
            agg.LatencyFiles++;
            agg.FirstCommitSeconds += commitAt;
            agg.FirstPartialSeconds += run.FirstPartialAt ?? commitAt;
        }
    }

    if (verbose)
        Console.WriteLine($"[{dataset}/{Path.GetFileName(wav)}] WER={Rate(dist, refWords.Length):P1} ({computeSec:F2}s)\n  REF: {reference}\n  HYP: {hypothesis}");
}

hypWriter?.Dispose();
pauseWriter?.Dispose();

// ── Report ──────────────────────────────────────────────────────────
var total = new DatasetAgg("TOTAL");
foreach (var agg in datasets.Values)
{
    total.Files += agg.Files;
    total.RefWords += agg.RefWords;
    total.Errors += agg.Errors;
    total.AudioSeconds += agg.AudioSeconds;
    total.ComputeSeconds += agg.ComputeSeconds;
    total.PreviewWords += agg.PreviewWords;
    total.PreviewErrors += agg.PreviewErrors;
    total.LatencyFiles += agg.LatencyFiles;
    total.FirstPartialSeconds += agg.FirstPartialSeconds;
    total.FirstCommitSeconds += agg.FirstCommitSeconds;
}

Console.WriteLine();
Console.WriteLine($"Provider: {provider}   Model: {modelDir}   EP: {ep}");
Console.WriteLine($"Audio   : {audioRoot}   Files: {total.Files}");
Console.WriteLine();
Console.WriteLine($"{"Dataset",-20} {"Files",6} {"Words",8} {"Err",6} {"WER",8} {"RTF",7} {"Speed",8}");
Console.WriteLine(new string('-', 68));
foreach (var agg in datasets.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
    PrintRow(agg);
Console.WriteLine(new string('-', 68));
PrintRow(total);

if (streaming)
    PrintStreamingRows(datasets.Values.OrderBy(d => d.Name, StringComparer.Ordinal).Append(total));

if (codec is not null)
{
    PrintCodecStats(codec, codecStats);
    Directory.Delete(codecTemp, recursive: true);
}

PrintTypicalErrors(errors, topN);

if (savePath is not null)
    WriteMarkdown(savePath, datasets, total, errors, topN, provider, modelDir, audioRoot);

return 0;

// ── Local functions ──────────────────────────────────────────────────

// Encodes with the same writer the app uses, then decodes with the reader used for
// offline re-recognition, so the measured WER is exactly what a stored recording gives.
static float[] RoundTrip(float[] samples, RecordingAudioFormat format, string tempDir, CodecStats stats)
{
    var basePath = Path.Combine(tempDir, "utt");
    var sw = Stopwatch.StartNew();
    var writer = RecordingAudioWriterFactory.Create(basePath, format, 16000, queued: false);
    // Feed 100 ms batches like the live capture does.
    for (int i = 0; i < samples.Length; i += 1600)
        writer.Append(samples.AsSpan(i, Math.Min(1600, samples.Length - i)));
    var result = writer.CompleteAsync().GetAwaiter().GetResult();
    writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
    stats.EncodeSeconds += sw.Elapsed.TotalSeconds;

    sw.Restart();
    var decoded = RecordingAudioReader.ReadMono(result.Path, 16000);
    stats.DecodeSeconds += sw.Elapsed.TotalSeconds;
    File.Delete(result.Path);

    stats.Files++;
    stats.AudioSeconds += samples.Length / 16000.0;
    stats.Bytes += result.Bytes;
    if (decoded.Length != samples.Length)
        stats.LengthMismatches++;
    // Signal-to-noise of the round trip (sample-aligned, so it is meaningful for WAV and Opus).
    int n = Math.Min(decoded.Length, samples.Length);
    double sig = 0, err = 0;
    for (int i = 0; i < n; i++)
    {
        sig += samples[i] * (double)samples[i];
        double d = samples[i] - decoded[i];
        err += d * d;
    }
    stats.SignalEnergy += sig;
    stats.ErrorEnergy += err;
    return decoded;
}

static void PrintCodecStats(RecordingAudioFormat format, CodecStats s)
{
    double hours = s.AudioSeconds / 3600;
    Console.WriteLine();
    Console.WriteLine($"Codec {format}: {s.Files} files, {s.AudioSeconds / 60:F1} min audio");
    Console.WriteLine($"  size      : {s.Bytes / 1e6 / hours:F1} MB/h = {s.Bytes / 1048576.0 / hours:F1} MiB/h ({s.Bytes * 8 / s.AudioSeconds / 1000:F1} kbit/s incl. container)");
    Console.WriteLine($"  encode    : {s.AudioSeconds / s.EncodeSeconds:F0}x real-time ({s.EncodeSeconds:F2}s)");
    Console.WriteLine($"  decode    : {s.AudioSeconds / s.DecodeSeconds:F0}x real-time ({s.DecodeSeconds:F2}s)");
    Console.WriteLine($"  SNR       : {10 * Math.Log10(s.SignalEnergy / Math.Max(s.ErrorEnergy, 1e-12)):F1} dB (waveform; low for Opus by design)");
    Console.WriteLine($"  length    : {(s.LengthMismatches == 0 ? "all decoded lengths match the input" : $"{s.LengthMismatches} files differ in length")}");
}

static void PrintRow(DatasetAgg agg)
{
    var speed = agg.Rtf > 0 ? 1.0 / agg.Rtf : 0;
    Console.WriteLine(
        $"{agg.Name,-20} {agg.Files,6} {agg.RefWords,8} {agg.Errors,6} {Rate(agg.Errors, agg.RefWords),8:P2} {agg.Rtf,7:F3} {speed,7:F1}x");
}

static void PrintStreamingRows(IEnumerable<DatasetAgg> aggs)
{
    Console.WriteLine();
    Console.WriteLine("Streaming (live display vs final transcript; first text = seconds of audio before anything is shown):");
    Console.WriteLine($"{"Dataset",-20} {"PrevWER",8} {"1stPrev",8} {"1stCommit",10}");
    foreach (var agg in aggs)
        Console.WriteLine($"{agg.Name,-20} {Rate(agg.PreviewErrors, agg.PreviewWords),8:P2} {agg.FirstPartialAvg,7:F2}s {agg.FirstCommitAvg,9:F2}s");
}

static void PrintTypicalErrors(ErrorCounters e, int topN)
{
    Console.WriteLine();
    Console.WriteLine("Typical model errors:");
    PrintTop("Substitutions (ref -> hyp)", e.Substitutions, topN);
    PrintTop("Insertions (extra words)", e.Insertions, topN);
    PrintTop("Deletions (missing words)", e.Deletions, topN);
}

static void PrintTop(string header, Dictionary<string, int> counts, int topN)
{
    var top = counts.OrderByDescending(kv => kv.Value).Take(topN).ToList();
    if (top.Count == 0) return;
    Console.WriteLine($"  {header}:");
    foreach (var (item, count) in top)
        Console.WriteLine($"    {count,5}  {item}");
}

static double Rate(int errors, int words) => words == 0 ? 0 : (double)errors / words;

static IStreamingSpeechRecognizer CreateRecognizer(string provider, string modelDir, string? lang, string ep, ParakeetStreamingOptions streamingOptions,
    NemotronStreamOptions nemotron) => provider switch
{
    "parakeet" => new ParakeetTdtRecognizer(
        modelDir,
        chunkSeconds: streamingOptions.ChunkSeconds,
        leftContextSeconds: streamingOptions.LeftContextSeconds,
        rightContextSeconds: streamingOptions.RightContextSeconds,
        executionProvider: ep,
        previewPartials: streamingOptions.Preview,
        silenceContextSeconds: streamingOptions.SilenceContextSeconds,
        onsetContextSeconds: streamingOptions.OnsetContextSeconds),
    "qwen3" => new Qwen3AsrRecognizer(modelDir, executionProvider: ep, language: lang),
    "qwen3-streaming" => new Qwen3AsrStreamingRecognizer(modelDir, executionProvider: ep, language: lang),
    // Raw input (no gain control): the accuracy of the model itself.
    "nemotron" => new NemotronUtteranceRecognizer(modelDir, ep, lang is null ? null : LanguageMapper.Resolve(lang), nemotron.UseVad,
        new NemotronRecognizerOptions { CpuThreads = nemotron.Threads, PauseState = nemotron.Pause, FlushClosingChunks = nemotron.ClosingChunks }),
    _ => throw new ArgumentException($"Unknown provider '{provider}'. Use 'parakeet', 'nemotron', 'qwen3' or 'qwen3-streaming'."),
};

static TranscribeRun Transcribe(IStreamingSpeechRecognizer recognizer, float[] samples, bool streaming, bool trace, string fileLabel)
{
    // Offline whole-utterance path (default for Parakeet/Qwen3) measures best-case
    // quality. --streaming exercises the buffer-based streaming path instead and
    // also prints the recognizer's revisable PartialText when it exposes one.
    if (!streaming && recognizer is ParakeetTdtRecognizer parakeet)
        return new TranscribeRun(parakeet.Transcribe(samples).Trim(), null, null, null);
    if (!streaming && recognizer is Qwen3AsrRecognizer qwen3)
        return new TranscribeRun(qwen3.Transcribe(samples).Trim(), null, null, null);

    recognizer.ResetStreamingState();
    var sb = new StringBuilder();
    var streamWatch = Stopwatch.StartNew();
    string lastPartial = "";
    double? firstPartialAt = null;
    double? firstCommitAt = null;
    if (trace)
        Console.WriteLine($"  [trace] {fileLabel} ({samples.Length / (double)recognizer.SampleRate:F2}s audio, chunk={recognizer.ChunkSamples})");

    for (int i = 0; i < samples.Length; i += recognizer.ChunkSamples)
    {
        int n = Math.Min(recognizer.ChunkSamples, samples.Length - i);
        var chunk = samples[i..(i + n)];
        double audioPos = (i + n) / (double)recognizer.SampleRate;

        var delta = recognizer.ProcessAudio(chunk);
        var partialText = recognizer.PartialText ?? "";

        if (!string.IsNullOrEmpty(delta))
        {
            sb.Append(delta);
            firstCommitAt ??= audioPos;
            if (trace)
                Console.WriteLine($"    t={streamWatch.Elapsed.TotalMilliseconds,9:F0}ms COMMIT  audio={audioPos,6:F2}s |{delta.Replace("\r", "").Replace("\n", " ")}");
        }

        if (partialText.Length > 0)
            firstPartialAt ??= audioPos;

        if (trace && partialText.Length > 0 && !string.Equals(partialText, lastPartial, StringComparison.Ordinal))
            Console.WriteLine($"    t={streamWatch.Elapsed.TotalMilliseconds,9:F0}ms partial audio={audioPos,6:F2}s |{partialText.Replace("\r", "").Replace("\n", " ")}");
        lastPartial = partialText;
    }

    // What a viewer saw just before the stream ended: committed text plus the revisable tail.
    var previewHypothesis = (sb + " " + lastPartial).Trim();

    var tail = recognizer.Flush();
    if (tail is not null)
    {
        sb.Append(tail);
        if (trace)
            Console.WriteLine($"    t={streamWatch.Elapsed.TotalMilliseconds,9:F0}ms (flush)          |{tail.Replace("\r", "").Replace("\n", " ")}");
    }

    if (trace)
        Console.WriteLine($"  [trace] done in {streamWatch.Elapsed.TotalSeconds:F2}s (RTF {streamWatch.Elapsed.TotalSeconds / (samples.Length / (double)recognizer.SampleRate):F3})");

    return new TranscribeRun(sb.ToString().Trim(), previewHypothesis, firstPartialAt, firstCommitAt);
}

static string[] Tokenize(string text) =>
    // Strip language-tag tokens (<ru-RU>, <en-US>, <unk>, ...) the same way the
    // NeMo reference evaluation does, so WER is comparable across pipelines.
    Regex.Replace(Regex.Replace(text.ToLowerInvariant(), @"<[^>\s]+>", " "), @"[^\p{L}\p{N}]+", " ")
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

static int Levenshtein(string[] a, string[] b)
{
    var d = new int[a.Length + 1, b.Length + 1];
    for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
    for (int j = 0; j <= b.Length; j++) d[0, j] = j;
    for (int i = 1; i <= a.Length; i++)
        for (int j = 1; j <= b.Length; j++)
        {
            int cost = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal) ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
    return d[a.Length, b.Length];
}

static List<(Op Op, string A, string B)> Align(string[] a, string[] b)
{
    int n = a.Length, m = b.Length;
    var d = new int[n + 1, m + 1];
    for (int i = 0; i <= n; i++) d[i, 0] = i;
    for (int j = 0; j <= m; j++) d[0, j] = j;
    for (int i = 1; i <= n; i++)
        for (int j = 1; j <= m; j++)
        {
            int cost = string.Equals(a[i - 1], b[j - 1], StringComparison.Ordinal) ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }

    var ops = new List<(Op, string, string)>();
    int x = n, y = m;
    while (x > 0 || y > 0)
    {
        if (x > 0 && y > 0 && d[x, y] == d[x - 1, y - 1] + (string.Equals(a[x - 1], b[y - 1], StringComparison.Ordinal) ? 0 : 1))
        {
            ops.Add(string.Equals(a[x - 1], b[y - 1], StringComparison.Ordinal) ? (Op.Ok, a[x - 1], b[y - 1]) : (Op.Sub, a[x - 1], b[y - 1]));
            x--; y--;
        }
        else if (y > 0 && d[x, y] == d[x, y - 1] + 1)
        {
            ops.Add((Op.Ins, "", b[y - 1]));
            y--;
        }
        else
        {
            ops.Add((Op.Del, a[x - 1], ""));
            x--;
        }
    }
    ops.Reverse();
    return ops;
}

static void CollectErrors(ErrorCounters e, List<(Op Op, string A, string B)> ops)
{
    foreach (var (op, a, b) in ops)
    {
        switch (op)
        {
            case Op.Sub: e.Substitutions[$"{a} -> {b}"] = e.Substitutions.GetValueOrDefault($"{a} -> {b}") + 1; break;
            case Op.Ins: e.Insertions[b] = e.Insertions.GetValueOrDefault(b) + 1; break;
            case Op.Del: e.Deletions[a] = e.Deletions.GetValueOrDefault(a) + 1; break;
        }
    }
}

static string? LanguageFromFolder(string folder)
{
    var name = new DirectoryInfo(folder).Name.ToLowerInvariant();
    return name is "ru" or "en" or "uk" or "de" or "fr" or "es" or "it" or "pt" or "pl" or "tr" or "zh" or "ja" or "ko" or "hi" or "ar"
        ? name
        : null;
}

static void WriteMarkdown(string path, Dictionary<string, DatasetAgg> datasets, DatasetAgg total, ErrorCounters e, int topN, string provider, string modelDir, string audioRoot)
{
    var sb = new StringBuilder();
    sb.AppendLine($"# WER report — {provider} ({Path.GetFileName(modelDir)})");
    sb.AppendLine();
    sb.AppendLine($"- Audio root: `{audioRoot}`");
    sb.AppendLine($"- Files: {total.Files}, reference words: {total.RefWords}");
    if (provider == "qwen3")
        sb.AppendLine("- Evaluation mode: offline whole-utterance transcription.");
    else if (provider == "qwen3-streaming")
        sb.AppendLine("- Evaluation mode: fixed-block streaming; each encoder block is encoded once and the decoder uses the latest bounded feature window with overlap-confirmed text.");
    sb.AppendLine();
    sb.AppendLine("| Dataset | Files | Words | Errors | WER | RTF | Speed |");
    sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
    foreach (var agg in datasets.Values.OrderBy(d => d.Name, StringComparer.Ordinal))
        sb.AppendLine($"| {agg.Name} | {agg.Files} | {agg.RefWords} | {agg.Errors} | {Rate(agg.Errors, agg.RefWords):P2} | {agg.Rtf:F3} | {(agg.Rtf > 0 ? 1.0 / agg.Rtf : 0):F1}x |");
    sb.AppendLine($"| **TOTAL** | {total.Files} | {total.RefWords} | {total.Errors} | {Rate(total.Errors, total.RefWords):P2} | {total.Rtf:F3} | {(total.Rtf > 0 ? 1.0 / total.Rtf : 0):F1}x |");
    sb.AppendLine();
    if (total.LatencyFiles > 0 || total.PreviewWords > 0)
    {
        sb.AppendLine("Streaming: preview WER = live display (committed + last revisable preview) vs the final transcript; first text = seconds of audio before anything is shown.");
        sb.AppendLine();
        sb.AppendLine("| Dataset | Preview WER | First preview | First commit |");
        sb.AppendLine("|---|---:|---:|---:|");
        foreach (var agg in datasets.Values.OrderBy(d => d.Name, StringComparer.Ordinal).Append(total))
            sb.AppendLine($"| {agg.Name} | {Rate(agg.PreviewErrors, agg.PreviewWords):P2} | {agg.FirstPartialAvg:F2}s | {agg.FirstCommitAvg:F2}s |");
        sb.AppendLine();
    }
    AppendMarkdownTop(sb, "Substitutions (ref → hyp)", e.Substitutions, topN);
    AppendMarkdownTop(sb, "Insertions (extra words)", e.Insertions, topN);
    AppendMarkdownTop(sb, "Deletions (missing words)", e.Deletions, topN);
    File.WriteAllText(path, sb.ToString());
    Console.WriteLine($"Saved: {path}");
}

static void AppendMarkdownTop(StringBuilder sb, string header, Dictionary<string, int> counts, int topN)
{
    var top = counts.OrderByDescending(kv => kv.Value).Take(topN).ToList();
    if (top.Count == 0) return;
    sb.AppendLine($"## {header}");
    sb.AppendLine();
    sb.AppendLine("| Count | Item |");
    sb.AppendLine("|---:|---|");
    foreach (var (item, count) in top)
        sb.AppendLine($"| {count} | {item} |");
    sb.AppendLine();
}

static float[] ReadWav16kMono(string path)
{
    using var br = new BinaryReader(File.OpenRead(path));
    br.BaseStream.Seek(22, SeekOrigin.Begin);
    short channels = br.ReadInt16();
    int sampleRate = br.ReadInt32();
    br.BaseStream.Seek(34, SeekOrigin.Begin);
    short bitsPerSample = br.ReadInt16();

    br.BaseStream.Seek(12, SeekOrigin.Begin);
    int dataSize = 0;
    while (br.BaseStream.Position + 8 <= br.BaseStream.Length)
    {
        var id = new string(br.ReadChars(4));
        int size = br.ReadInt32();
        if (id == "data") { dataSize = size; break; }
        br.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
    }
    if (dataSize == 0) throw new InvalidDataException("No data chunk found");
    if (bitsPerSample != 16) throw new InvalidDataException($"Expected 16-bit PCM, got {bitsPerSample}");

    int frames = dataSize / 2 / channels;
    var output = new float[frames];
    for (int i = 0; i < frames; i++)
    {
        short s = br.ReadInt16();
        for (int c = 1; c < channels; c++) br.ReadInt16();
        output[i] = s / 32768f;
    }
    if (sampleRate != 16000) throw new InvalidDataException($"Expected 16 kHz, got {sampleRate}");
    return output;
}

enum Op { Ok, Sub, Ins, Del }

/// <summary>Parakeet buffer-streaming parameters (see ParakeetTdtRecognizer constructor).</summary>
sealed class ParakeetStreamingOptions
{
    public double ChunkSeconds = ParakeetTdtRecognizer.DefaultChunkSeconds;
    public double RightContextSeconds = ParakeetTdtRecognizer.DefaultRightContextSeconds;
    public double LeftContextSeconds = ParakeetTdtRecognizer.DefaultLeftContextSeconds;
    public double SilenceContextSeconds = ParakeetTdtRecognizer.DefaultSilenceContextSeconds;
    public double OnsetContextSeconds = ParakeetTdtRecognizer.DefaultOnsetContextSeconds;
    public bool Preview = true;
}

/// <summary>Nemotron recognizer options (see NemotronRecognizerOptions).</summary>
sealed class NemotronStreamOptions
{
    public bool UseVad;
    public int? Threads;
    public NemotronPauseStateOptions Pause = NemotronPauseStateOptions.Parity;
    public int ClosingChunks;
}

/// <param name="Hypothesis">Final transcript.</param>
/// <param name="PreviewHypothesis">Committed text + last revisable preview just before flush (streaming only).</param>
/// <param name="FirstPartialAt">Audio position (s) when partial text first appeared (streaming only).</param>
/// <param name="FirstCommitAt">Audio position (s) of the first committed delta (streaming only).</param>
sealed record TranscribeRun(string Hypothesis, string? PreviewHypothesis, double? FirstPartialAt, double? FirstCommitAt);

sealed class DatasetAgg
{
    public DatasetAgg(string name) => Name = name;
    public string Name { get; }
    public int Files;
    public int RefWords;
    public int Errors;
    public double AudioSeconds;
    public double ComputeSeconds;
    public int PreviewWords;
    public int PreviewErrors;
    public int LatencyFiles;
    public double FirstPartialSeconds;
    public double FirstCommitSeconds;
    public double Wer => RefWords == 0 ? 0 : (double)Errors / RefWords;
    public double Rtf => AudioSeconds == 0 ? 0 : ComputeSeconds / AudioSeconds;
    public double FirstPartialAvg => LatencyFiles == 0 ? 0 : FirstPartialSeconds / LatencyFiles;
    public double FirstCommitAvg => LatencyFiles == 0 ? 0 : FirstCommitSeconds / LatencyFiles;
}

sealed class CodecStats
{
    public int Files;
    public double AudioSeconds;
    public long Bytes;
    public double EncodeSeconds;
    public double DecodeSeconds;
    public int LengthMismatches;
    public double SignalEnergy;
    public double ErrorEnergy;
}

sealed class ErrorCounters
{
    public Dictionary<string, int> Substitutions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Insertions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> Deletions { get; } = new(StringComparer.OrdinalIgnoreCase);
}
