#:property TargetFramework=net10.0
#:property AllowUnsafeBlocks=true
#:property PublishAot=false
#:property JsonSerializerIsReflectionEnabledByDefault=true
#:property NoWarn=IL2026;IL3050
#:package Microsoft.ML.OnnxRuntimeGenAI@0.16.0

// ONNX Runtime GenAI 0.16.0 oracle for the Nemotron streaming ASR models (the permanent parity reference of the plain-ORT migration).
//
//   dotnet run genai_oracle.cs -- golden --model <dir> --variant int4-c056 --clips clips.json --set primary --out <dir>
//                                 --root TestAudio=<path> --root golden=<path> [--threads 4] [--only id,id] [--patterns aligned,ragged3200,irregular]
//   dotnet run genai_oracle.cs -- vad    --model <dir> --variant int4-c056 --scenarios vad_scenarios.json --out <dir> --root ... [--threads 4]
//   dotnet run genai_oracle.cs -- perf   --model <dir> --variant int4-c056 --clips clips.json --ids a,b,c --root ... --threads 4 [--reps 3] --out perf.json
//
// One StreamingProcessor + Generator pair per clip and feeding pattern (a fresh stream, nothing carried over). The model is loaded
// once per process. CPU provider, session options exactly as the apps apply them (inter_op 1, spinning off, ORT_SEQUENTIAL).
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntimeGenAI;

var opts = new Dictionary<string, string>();
var roots = new Dictionary<string, string>();
for (int i = 1; i < args.Length; i++)
{
    if (!args[i].StartsWith("--")) continue;
    string key = args[i][2..];
    string val = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true";
    if (key == "root") { int eq = val.IndexOf('='); roots[val[..eq]] = val[(eq + 1)..]; }
    else opts[key] = val;
}
string mode = args[0];
string modelDir = opts["model"];
string variant = opts["variant"];
int threads = opts.TryGetValue("threads", out var th) ? int.Parse(th) : 4;
string outPath = opts["out"];

var jsonOut = new JsonSerializerOptions { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
const int Rate = 16000;
int[] IrregularCycle = [800, 3200, 160, 5000, 8960, 1, 2400, 7200, 320, 4480];

// ---------------------------------------------------------------------------------------------------------------------
// model
var loadSw = Stopwatch.StartNew();
var cfgJson = JsonNode.Parse(File.ReadAllText(Path.Combine(modelDir, "genai_config.json")))!;
int chunkSamples = cfgJson["model"]!["chunk_samples"]!.GetValue<int>();
foreach (var key in new[] { "encoder", "decoder" })
{
    cfgJson["model"]![key]!.AsObject()["session_options"] = new JsonObject
    {
        ["intra_op_num_threads"] = threads,
        ["inter_op_num_threads"] = 1,
        ["session.intra_op.allow_spinning"] = "0",
        ["session.inter_op.allow_spinning"] = "0",
        ["session.force_spinning_stop"] = "1",
        ["execution_mode"] = "ORT_SEQUENTIAL",
    };
}
using var config = new Config(modelDir);
config.Overlay(cfgJson.ToJsonString());
config.ClearProviders();
using var model = new Model(config);
using var tokenizer = new Tokenizer(model);
loadSw.Stop();
Console.WriteLine($"model loaded in {loadSw.Elapsed.TotalSeconds:F2} s: {variant} chunkSamples={chunkSamples} threads={threads} workingSet={Process.GetCurrentProcess().WorkingSet64 / 1048576} MB");

string Versions()
{
    string Ver(string file)
    {
        var p = Directory.GetFiles(AppContext.BaseDirectory, file, SearchOption.AllDirectories).FirstOrDefault();
        return p is null ? "?" : FileVersionInfo.GetVersionInfo(p).FileVersion ?? "?";
    }
    return $"managed {typeof(Model).Assembly.GetName().Version}, onnxruntime-genai.dll {Ver("onnxruntime-genai.dll")}, onnxruntime.dll {Ver("onnxruntime.dll")}";
}
string versions = Versions();

// ---------------------------------------------------------------------------------------------------------------------
// feed / decode engine
(List<Call> calls, int[] flushIds, string[] flushPieces, bool flushed, string text, int tokens, double seconds, int flushPad, List<int> batches)
    Run(float[] audio, string? langId, bool vad, Func<int, int> batchSize)
{
    using var processor = new StreamingProcessor(model);
    processor.SetOption("use_vad", vad ? "true" : "false");
    using var tokStream = tokenizer.CreateStream();
    using var gp = new GeneratorParams(model);
    using var gen = new Generator(model, gp);
    if (langId is not null) gen.SetRuntimeOption("lang_id", langId);

    var calls = new List<Call>();
    var batches = new List<int>();
    var text = new StringBuilder();
    int tokens = 0;

    (int[], string[]) Decode()
    {
        var ids = new List<int>();
        var pieces = new List<string>();
        while (!gen.IsDone())
        {
            gen.GenerateNextToken();
            var toks = gen.GetNextTokens();
            if (toks.Length > 0)
            {
                ids.Add(toks[0]);
                var t = tokStream.Decode(toks[0]);
                pieces.Add(t);
                text.Append(t);
            }
        }
        tokens += ids.Count;
        return (ids.ToArray(), pieces.ToArray());
    }

    long buffered = 0, fed = 0;
    int consumed = 0;
    var sw = Stopwatch.StartNew();
    int call = 0;
    for (int pos = 0; pos < audio.Length; call++)
    {
        int n = Math.Min(batchSize(call), audio.Length - pos);
        var chunk = new float[n];
        Array.Copy(audio, pos, chunk, 0, n);
        pos += n;
        fed += n;
        buffered += n;
        batches.Add(n);
        bool expect = buffered >= chunkSamples;
        int idx = -1;
        if (expect) { idx = consumed++; buffered -= chunkSamples; }
        using var inputs = processor.Process(chunk);
        if (inputs is null)
        {
            calls.Add(new Call(call, fed, n, expect, false, idx, [], []));
            continue;
        }
        gen.SetInputs(inputs);
        var (ids, pieces) = Decode();
        calls.Add(new Call(call, fed, n, expect, true, idx, ids, pieces));
    }
    int[] fIds = []; string[] fPieces = []; bool flushed = false;
    int flushPad = 0;
    using (var flushInputs = processor.Flush())
    {
        if (flushInputs is not null)
        {
            flushed = true;
            flushPad = chunkSamples - (int)buffered;
            gen.SetInputs(flushInputs);
            (fIds, fPieces) = Decode();
        }
    }
    sw.Stop();
    return (calls, fIds, fPieces, flushed, text.ToString(), tokens, sw.Elapsed.TotalSeconds, flushPad, batches);
}

object Rle(List<int> batches)
{
    var rle = new List<int[]>();
    foreach (var b in batches)
    {
        if (rle.Count > 0 && rle[^1][0] == b) rle[^1][1]++;
        else rle.Add([b, 1]);
    }
    return rle;
}

Func<int, int> Pattern(string name) => name switch
{
    "aligned" => _ => chunkSamples,
    "ragged3200" => _ => 3200,
    "ragged800" => _ => 800,
    "irregular" => c => IrregularCycle[c % IrregularCycle.Length],
    _ => throw new ArgumentException("pattern " + name),
};

string? LangOf(JsonNode clip) => clip["lang"]?.GetValue<string>() is { } l && l != "-" ? l : null;

float[] LoadAudio(JsonNode clip)
{
    string root = roots[clip["root"]!.GetValue<string>()];
    string path = Path.Combine(root, clip["path"]!.GetValue<string>().Replace('/', Path.DirectorySeparatorChar));
    return ReadWav(path);
}

// ---------------------------------------------------------------------------------------------------------------------
if (mode == "golden")
{
    var clips = JsonNode.Parse(File.ReadAllText(opts["clips"]))!["clips"]!.AsArray();
    string set = opts.GetValueOrDefault("set", "primary");
    var only = opts.TryGetValue("only", out var o) ? o.Split(',').ToHashSet() : null;
    var patterns = opts.GetValueOrDefault("patterns", "aligned,ragged3200,irregular").Split(',');
    Directory.CreateDirectory(outPath);
    int done = 0, mismatches = 0;
    double totalAudio = 0, totalSec = 0;
    foreach (var clip in clips)
    {
        string id = clip!["id"]!.GetValue<string>();
        var sets = clip["sets"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        if (only is not null ? !only.Contains(id) : !sets.Contains(set)) continue;
        var audio = LoadAudio(clip);
        string? lang = LangOf(clip);

        // canonical run: aligned, chunk_samples per feed call (the oracle_multi.cs format)
        var a = Run(audio, lang, false, Pattern("aligned"));
        var canonical = a.calls.Where(c => c.Processed).Select(c => c.Ids).ToList();
        bool allExpected = a.calls.All(c => c.Expect == c.Processed);
        var chunksJson = a.calls.Select(c => new Dictionary<string, object?>
        {
            ["feed"] = c.Feed, ["processed"] = c.Processed, ["chunk"] = c.Processed ? c.Chunk : -1, ["ids"] = c.Ids, ["pieces"] = c.Pieces,
        }).ToList();
        if (a.flushed)
            chunksJson.Add(new Dictionary<string, object?> { ["feed"] = "flush", ["processed"] = true, ["chunk"] = -1, ["ids"] = a.flushIds, ["pieces"] = a.flushPieces });
        var result = new Dictionary<string, object?>
        {
            ["schema"] = 1,
            ["variant"] = variant,
            ["model"] = Path.GetFileName(modelDir.TrimEnd('/', '\\')),
            ["clip"] = id,
            ["audioPath"] = $"{clip["root"]!.GetValue<string>()}/{clip["path"]!.GetValue<string>()}",
            ["chunkSamples"] = chunkSamples,
            ["audioSamples"] = audio.Length,
            ["langId"] = lang,
            ["vad"] = false,
            ["threads"] = threads,
            ["seconds"] = a.seconds,
            ["text"] = a.text,
            ["tokenCount"] = a.tokens,
            ["feedBatches"] = Rle(a.batches),
            ["flushPaddedSamples"] = a.flushed ? a.flushPad : 0,
            ["chunks"] = chunksJson,
        };

        var other = new Dictionary<string, object?>();
        bool same = true;
        foreach (var pat in patterns.Where(p => p != "aligned"))
        {
            var r = Run(audio, lang, false, Pattern(pat));
            var ids = r.calls.Where(c => c.Processed).Select(c => c.Ids).ToList();
            bool eq = ids.Count == canonical.Count && ids.Zip(canonical).All(t => t.First.SequenceEqual(t.Second))
                      && r.flushed == a.flushed && r.flushIds.SequenceEqual(a.flushIds) && r.text == a.text;
            if (!eq) { same = false; mismatches++; }
            other[pat] = new Dictionary<string, object?>
            {
                ["feedBatches"] = Rle(r.batches),
                ["identicalToAligned"] = eq,
                ["seconds"] = r.seconds,
                ["chunkIds"] = eq ? null : ids,           // identical -> nothing to store: the aligned `chunks` already are the answer
                ["flushIds"] = eq ? null : r.flushIds,
                ["text"] = eq ? null : r.text,
            };
        }
        result["otherFeedPatterns"] = other;
        result["allCallsConsistentWithBuffer"] = allExpected;
        File.WriteAllText(Path.Combine(outPath, id + ".json"), JsonSerializer.Serialize(result, jsonOut));
        done++;
        totalAudio += audio.Length / (double)Rate;
        totalSec += a.seconds;
        Console.WriteLine($"{variant,-10} {id,-28} {audio.Length / (double)Rate,6:F1}s tokens={a.tokens,4} aligned={a.seconds,6:F1}s patterns={(same ? "identical" : "MISMATCH")} text=[{Trim(a.text)}]");
    }
    Console.WriteLine($"done: {done} clips, {totalAudio:F0} s audio, aligned decode {totalSec:F0} s, pattern mismatches {mismatches}");
}
else if (mode == "vad")
{
    var scenarios = JsonNode.Parse(File.ReadAllText(opts["scenarios"]))!["scenarios"]!.AsArray();
    Directory.CreateDirectory(outPath);
    foreach (var sc in scenarios)
    {
        string id = sc!["id"]!.GetValue<string>();
        var audio = LoadAudio(sc);
        string? lang = LangOf(sc);
        foreach (var feed in sc["feeds"]!.AsArray().Select(f => f!.GetValue<int>()))
        {
            int batch = feed == 0 ? chunkSamples : feed;       // 0 = chunk aligned
            var r = Run(audio, lang, true, _ => batch);
            var dropped = r.calls.Where(c => c.Expect && !c.Processed).Select(c => c.Chunk).ToList();
            var emitted = r.calls.Where(c => c.Processed).ToList();
            var result = new Dictionary<string, object?>
            {
                ["schema"] = 1,
                ["variant"] = variant,
                ["model"] = Path.GetFileName(modelDir.TrimEnd('/', '\\')),
                ["scenario"] = id,
                ["audioPath"] = $"{sc["root"]!.GetValue<string>()}/{sc["path"]!.GetValue<string>()}",
                ["chunkSamples"] = chunkSamples,
                ["audioSamples"] = audio.Length,
                ["langId"] = lang,
                ["vad"] = true,
                ["vadConfig"] = JsonNode.Parse(File.ReadAllText(Path.Combine(modelDir, "genai_config.json")))!["model"]!["vad"]!.ToJsonString(),
                ["threads"] = threads,
                ["feedSamplesPerCall"] = batch,
                ["feedBatches"] = Rle(r.batches),
                ["chunksConsumed"] = r.calls.Count(c => c.Expect),
                ["chunksEmitted"] = emitted.Count(c => c.Chunk >= 0),
                ["droppedChunkIndexes"] = dropped,
                ["droppedRanges"] = Ranges(dropped).Select(t => new[] { t.From, t.To }).ToList(),
                ["emittedWithoutAvailableChunk"] = r.calls.Count(c => !c.Expect && c.Processed),
                ["leftoverSamplesBeforeFlush"] = r.flushed ? chunkSamples - r.flushPad : 0,
                ["flushPaddedSamples"] = r.flushed ? r.flushPad : 0,
                ["text"] = r.text,
                ["tokenCount"] = r.tokens,
                // every call that consumed a chunk: processed true = returned a chunk (decoded), false = null although a chunk was available (dropped by VAD)
                ["chunkCalls"] = r.calls.Where(c => c.Expect).Select(c => new Dictionary<string, object?>
                    { ["chunk"] = c.Chunk, ["call"] = c.Feed, ["fedSamples"] = c.Fed, ["processed"] = c.Processed, ["ids"] = c.Ids, ["pieces"] = c.Pieces }).ToList(),
                ["flush"] = r.flushed ? new Dictionary<string, object?> { ["ids"] = r.flushIds, ["pieces"] = r.flushPieces } : null,
                ["nullCallsWithoutChunk"] = r.calls.Count(c => !c.Expect),
            };
            string name = $"{id}.feed{feed}.json";
            File.WriteAllText(Path.Combine(outPath, name), JsonSerializer.Serialize(result, jsonOut));
            Console.WriteLine($"{variant,-10} {id,-24} feed={batch,5} consumed={r.calls.Count(c => c.Expect),3} emitted={emitted.Count(c => c.Chunk >= 0),3} dropped=[{string.Join(",", Ranges(dropped).Select(t => t.From == t.To ? $"{t.From}" : $"{t.From}-{t.To}"))}] tokens={r.tokens}");
        }
    }
}
else if (mode == "quirks")
{
    // GenAI feeding quirks a re-implementation may or may not want to copy (A4 BigBatchScenario, with token ids):
    //  - Process() consumes at most ONE chunk per call, the rest stays buffered
    //  - Flush() zero-pads the buffer to ONE chunk and silently drops everything beyond it
    var clips = JsonNode.Parse(File.ReadAllText(opts["clips"]))!["clips"]!.AsArray();
    var clip = clips.First(c => c!["id"]!.GetValue<string>() == opts["ids"])!;
    var audio = LoadAudio(clip);
    string? lang = LangOf(clip);
    object Scenario(string name, int[] feeds, int flushes, int extraSingleSampleCalls)
    {
        using var processor = new StreamingProcessor(model);
        processor.SetOption("use_vad", "false");
        using var tokStream = tokenizer.CreateStream();
        using var gp = new GeneratorParams(model);
        using var gen = new Generator(model, gp);
        if (lang is not null) gen.SetRuntimeOption("lang_id", lang);
        var steps = new List<Dictionary<string, object?>>();
        int[] Decode()
        {
            var ids = new List<int>();
            while (!gen.IsDone()) { gen.GenerateNextToken(); var t = gen.GetNextTokens(); if (t.Length > 0) ids.Add(t[0]); }
            return ids.ToArray();
        }
        int pos = 0;
        void Feed(int n, string label)
        {
            var part = new float[n];
            Array.Copy(audio, Math.Min(pos, audio.Length), part, 0, Math.Min(n, Math.Max(0, audio.Length - pos)));
            pos += n;
            using var inputs = processor.Process(part);
            int[] ids = [];
            if (inputs is not null) { gen.SetInputs(inputs); ids = Decode(); }
            steps.Add(new() { ["call"] = label, ["samples"] = n, ["returnedChunk"] = inputs is not null, ["ids"] = ids });
        }
        foreach (var f in feeds) Feed(f, "process");
        for (int i = 0; i < extraSingleSampleCalls; i++) Feed(1, "process-1-sample");
        for (int i = 0; i < flushes; i++)
        {
            using var fl = processor.Flush();
            int[] ids = [];
            if (fl is not null) { gen.SetInputs(fl); ids = Decode(); }
            steps.Add(new() { ["call"] = "flush", ["samples"] = 0, ["returnedChunk"] = fl is not null, ["ids"] = ids });
        }
        return new { name, steps };
    }
    int cs = chunkSamples;
    var result = new
    {
        schema = 1, variant, clip = opts["ids"], chunkSamples = cs, langId = lang, threads,
        scenarios = new[]
        {
            Scenario("5 chunks in one call, then 7 one-sample calls, then 2 flushes", [5 * cs], 2, 7),
            Scenario("3 chunks in one call, then 3 flushes (excess beyond one chunk is truncated)", [3 * cs], 3, 0),
            Scenario("whole clip in ONE call, then 2 flushes", [audio.Length], 2, 0),
            Scenario("chunk + 1 sample in one call, then 2 flushes", [cs + 1], 2, 0),
        },
    };
    File.WriteAllText(outPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine("quirks written to " + outPath);
}
else if (mode == "perf")
{
    var clips = JsonNode.Parse(File.ReadAllText(opts["clips"]))!["clips"]!.AsArray();
    var ids = opts["ids"].Split(',');
    int reps = opts.TryGetValue("reps", out var rp) ? int.Parse(rp) : 3;
    var proc = Process.GetCurrentProcess();
    var rows = new List<object>();
    foreach (var id in ids)
    {
        var clip = clips.First(c => c!["id"]!.GetValue<string>() == id)!;
        var audio = LoadAudio(clip);
        double dur = audio.Length / (double)Rate;
        var times = new List<double>();
        var cpus = new List<double>();
        double first = 0, firstCpu = 0;
        // one warm-up run, then `reps` timed runs; cpu = process CPU time (user+kernel) spent during the run, less sensitive to machine contention than wall time
        for (int r = 0; r <= reps; r++)
        {
            proc.Refresh();
            var cpu0 = proc.TotalProcessorTime;
            var res = Run(audio, LangOf(clip), false, Pattern("aligned"));
            proc.Refresh();
            double cpu = (proc.TotalProcessorTime - cpu0).TotalSeconds;
            if (r == 0) { first = res.seconds; firstCpu = cpu; } else { times.Add(res.seconds); cpus.Add(cpu); }
        }
        var sorted = times.OrderBy(t => t).ToList();
        double median = sorted[sorted.Count / 2];
        double medianCpu = cpus.OrderBy(t => t).ToList()[cpus.Count / 2];
        proc.Refresh();
        rows.Add(new
        {
            clip = id, audioSeconds = Math.Round(dur, 3), warmupSeconds = Math.Round(first, 3), warmupCpuSeconds = Math.Round(firstCpu, 3),
            runs = times.Select(t => Math.Round(t, 3)).ToArray(), cpuRuns = cpus.Select(t => Math.Round(t, 3)).ToArray(),
            medianSeconds = Math.Round(median, 3), medianRtf = Math.Round(median / dur, 4), medianCpuSeconds = Math.Round(medianCpu, 3),
            peakWorkingSetMB = proc.PeakWorkingSet64 / 1048576,
        });
        Console.WriteLine($"{variant,-10} thr={threads,2} {id,-24} {dur,6:F1}s warm-up {first,6:F2}s runs [{string.Join(", ", times.Select(t => t.ToString("F2")))}] median {median:F2}s RTF {median / dur:F3} cpu {medianCpu:F1}s peakWS {proc.PeakWorkingSet64 / 1048576} MB");
    }
    proc.Refresh();
    File.WriteAllText(outPath, JsonSerializer.Serialize(new
    {
        variant, threads, versions, modelLoadSeconds = Math.Round(loadSw.Elapsed.TotalSeconds, 3), peakWorkingSetMB = proc.PeakWorkingSet64 / 1048576, results = rows,
    }, new JsonSerializerOptions { WriteIndented = true }));
}

Console.WriteLine($"versions: {versions}");
return 0;

// ---------------------------------------------------------------------------------------------------------------------
static string Trim(string s) => s.Length > 90 ? s[..90] + "..." : s;

static IEnumerable<(int From, int To)> Ranges(List<int> sorted)
{
    if (sorted.Count == 0) yield break;
    int from = sorted[0], prev = sorted[0];
    for (int i = 1; i < sorted.Count; i++)
    {
        if (sorted[i] == prev + 1) { prev = sorted[i]; continue; }
        yield return (from, prev);
        from = prev = sorted[i];
    }
    yield return (from, prev);
}

static float[] ReadWav(string path)
{
    var b = File.ReadAllBytes(path);
    int pos = 12, fmt = 1, bits = 16;
    while (pos + 8 <= b.Length)
    {
        string id = Encoding.ASCII.GetString(b, pos, 4);
        int size = BitConverter.ToInt32(b, pos + 4);
        if (id == "fmt ") { fmt = BitConverter.ToUInt16(b, pos + 8); bits = BitConverter.ToInt16(b, pos + 22); }
        if (id == "data")
        {
            int bytes = Math.Min(size, b.Length - pos - 8);
            if (fmt == 1 && bits == 16)
            {
                int n = bytes / 2;
                var x = new float[n];
                for (int i = 0; i < n; i++) x[i] = BitConverter.ToInt16(b, pos + 8 + 2 * i) / 32768f;
                return x;
            }
            if (fmt == 3 && bits == 32)
            {
                var x = new float[bytes / 4];
                Buffer.BlockCopy(b, pos + 8, x, 0, x.Length * 4);
                return x;
            }
            throw new InvalidDataException($"{path}: unsupported wav format {fmt}/{bits}");
        }
        pos += 8 + size + (size & 1);
    }
    throw new InvalidDataException(path);
}

sealed record Call(int Feed, long Fed, int Samples, bool Expect, bool Processed, int Chunk, int[] Ids, string[] Pieces);
