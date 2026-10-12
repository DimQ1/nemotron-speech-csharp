// DiarBench — C# ONNX Runtime parity + real-time benchmark for Nemotron-3-Diarization.
//
//   dotnet run -c Release --project tools/DiarBench -- --wav conv.wav --mode low_latency
//       [--model-dir models/nemotron-3-diarization-onnx] [--step diar_step.onnx|diar_step.int8.onnx]
//       [--threads 4] [--dump probs.f32] [--repeat 1]
//
// Simulates live streaming: per step it computes only the new mel frames (diar_frontend.onnx on the
// pre-emphasized, zero-left-padded signal), runs diar_step.onnx on [cache | fifo | chunk+lookahead] and
// updates the AOSC state in C#. Reports load time, per-step latency, RTF, peak memory.
// --dump writes the 10 ms probabilities (float32, T x 8, row-major) for parity with run_backends.py.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using DiarBench;

var opt = Args.Parse(args);
string modelDir = opt.Get("model-dir", Path.Combine(FindRepo(), "models", "nemotron-3-diarization-onnx"));
string stepFile = opt.Get("step", "diar_step.onnx");
string mode = opt.Get("mode", "low_latency");
int threads = int.Parse(opt.Get("threads", "0"));
int repeat = int.Parse(opt.Get("repeat", "1"));
string? dump = opt.GetOrNull("dump");
if (opt.GetOrNull("probe") is string probeArg)
{
    using var ps = new InferenceSession(Path.Combine(modelDir, stepFile));
    var rnd = new Random(1);
    foreach (var ncs in probeArg.Split(','))
    {
        int pnc = int.Parse(ncs);
        var f = Enumerable.Range(0, 104 * 128).Select(_ => (float)rnd.NextDouble() * 4 - 10).ToArray();
        var c = Enumerable.Range(0, pnc * 512).Select(_ => (float)rnd.NextDouble() - 0.5f).ToArray();
        using var a = OrtValue.CreateTensorValueFromMemory(f, [1, 104, 128]);
        using var b = OrtValue.CreateTensorValueFromMemory(c, [1, pnc, 512]);
        using var o = ps.Run(Ort.Run, new Dictionary<string, OrtValue> { ["features"] = a, ["cache_embeds"] = b }, ["chunk_embeds", "probs", "frame_probs"]);
        Console.WriteLine($"probe nc={pnc} ok, probs={o[1].GetTensorTypeAndShape().Shape.Aggregate("", (x, y) => x + y + "x")}");
    }
    return 0;
}
string wavPath = opt.GetOrNull("wav") ?? throw new ArgumentException("--wav required");

using var cfgDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDir, "diar_config.json")));
var cfg = cfgDoc.RootElement;
float[] silence = cfg.GetProperty("silence_embed").EnumerateArray().Select(e => e.GetSingle()).ToArray();
JsonElement geo = mode == "offline" ? cfg.GetProperty("offline") : cfg.GetProperty("modes").GetProperty(mode);
int chunk = geo.GetProperty("chunk_len").GetInt32();
int rc = geo.GetProperty("right_context").GetInt32();
int fifoLen = geo.GetProperty("fifo_len").GetInt32();
int updPeriod = geo.GetProperty("update_period").GetInt32();

var so = new SessionOptions
{
    GraphOptimizationLevel = opt.Get("opt", "all") switch
    {
        "basic" => GraphOptimizationLevel.ORT_ENABLE_BASIC,
        "extended" => GraphOptimizationLevel.ORT_ENABLE_EXTENDED,
        "none" => GraphOptimizationLevel.ORT_DISABLE_ALL,
        _ => GraphOptimizationLevel.ORT_ENABLE_ALL,
    },
};
if (threads > 0) { so.IntraOpNumThreads = threads; so.InterOpNumThreads = 1; }

long ws0 = Process.GetCurrentProcess().WorkingSet64;
var sw = Stopwatch.StartNew();
using var step = new InferenceSession(Path.Combine(modelDir, stepFile), so);
using var frontend = new InferenceSession(Path.Combine(modelDir, "diar_frontend.onnx"), so);
double loadMs = sw.Elapsed.TotalMilliseconds;
long wsLoaded = Process.GetCurrentProcess().WorkingSet64;

float[] audio = Wav.ReadMono16k(wavPath);
double audioSec = audio.Length / 16000.0;
Console.WriteLine($"model={stepFile} mode={mode} chunk={chunk} rc={rc} fifo={fifoLen} upd={updPeriod} threads={(threads > 0 ? threads : Environment.ProcessorCount)} " +
                  $"audio={audioSec:F1}s load={loadMs:F0}ms ws_after_load={wsLoaded / 1e6:F0}MB (+{(wsLoaded - ws0) / 1e6:F0})");

List<double> stepMs = [], feMs = [], updMs = [];
List<int> stepCached = [];
float[] probs = [];
double totalMs = 0;
for (int r = 0; r < repeat; r++)
{
    stepMs.Clear(); feMs.Clear(); updMs.Clear(); stepCached.Clear();
    var total = Stopwatch.StartNew();
    probs = mode == "offline" ? RunOffline() : RunStreaming();
    totalMs = total.Elapsed.TotalMilliseconds;
}

var proc = Process.GetCurrentProcess();
proc.Refresh();
double hop = chunk * 0.08;
// sustained = every step once the speaker cache has been filled/compressed the first time (cached frames
// then oscillate between spkcache+fifo-update_period and spkcache+fifo) — the long-run per-hop cost.
int firstFull = stepCached.FindIndex(n => n >= 264);
var steady = (firstFull < 0 ? stepMs : stepMs.Skip(firstFull)).OrderBy(x => x).ToArray();
var all = stepMs.OrderBy(x => x).ToArray();
Console.WriteLine($"steps={stepMs.Count} total={totalMs:F0}ms RTF={totalMs / 1000 / audioSec:F4} (RTFx {audioSec * 1000 / totalMs:F1})");
Console.WriteLine($"step model ms: mean={all.Average():F1} p50={Pct(all, 50):F1} p95={Pct(all, 95):F1} max={all[^1]:F1}");
Console.WriteLine($"steady-state (cache full, cached {stepCached.Skip(Math.Max(0, firstFull)).Min()}..{stepCached.Max()} frames, n={steady.Length}): mean={steady.Average():F1} p95={Pct(steady, 95):F1} max={steady[^1]:F1} ms " +
                  $"per {hop:F2}s hop -> sustained RTF={steady.Average() / 1000 / hop:F3}, worst-step RTF={steady[^1] / 1000 / hop:F3}");
Console.WriteLine($"frontend ms/step mean={feMs.Average():F2}  aosc update ms/step mean={updMs.Average():F2} max={updMs.Max():F2}");
Console.WriteLine($"peak working set={proc.PeakWorkingSet64 / 1e6:F0}MB private={proc.PrivateMemorySize64 / 1e6:F0}MB managed={GC.GetTotalMemory(false) / 1e6:F1}MB");
if (dump != null)
{
    using var fs = File.Create(dump);
    fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(probs.AsSpan()));
    Console.WriteLine($"dumped {probs.Length / 8} x 8 -> {dump}");
}
return 0;

// ---------------------------------------------------------------------------------------------
float[] RunStreaming()
{
    // global padded signal: 256 zeros + preemph(audio); mel frame k = padded[k*160 .. k*160+512)
    var padded = Frontend.Padded(audio, rightPad: 0);
    int tValid = audio.Length / 160;
    int c8 = chunk * 8, f = (chunk + rc) * 8;
    var mel = new List<float[]>();
    var state = new AoscState(fifoLen, updPeriod, silence);
    var outProbs = new List<float>();
    int samplesPerChunk = f * 160 + 400;
    int k = 0;
    while (true)
    {
        int ms = k * c8;
        bool last;
        int me;
        if (k == 0 || ms * 160 - 256 + samplesPerChunk <= audio.Length) { me = ms + f; last = false; }
        else if (ms < tValid) { me = tValid; last = true; }
        else break;
        if (k == 0 && me > tValid) { me = tValid; last = true; }

        var t0 = Stopwatch.GetTimestamp();
        Frontend.Extend(frontend, padded, mel, me, audio.Length);
        feMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);

        int nIn = (me - ms + 7) / 8;
        int nChunk = last ? nIn : chunk;
        int nc = state.CachedFrames;
        var feats = new float[nIn * 8 * 128];
        for (int i = ms; i < me; i++) mel[i].CopyTo(feats, (i - ms) * 128);
        var (ce, p10, p80) = RunStep(feats, nIn * 8, state, nc);
        var t1 = Stopwatch.GetTimestamp();
        state.Update(ce.AsSpan(0, nChunk * 512), p80);
        if (Environment.GetEnvironmentVariable("DIAR_TRACE") == "1") Console.Error.WriteLine($"  -> cache={state.CacheFrames} fifo={state.FifoFrames} ce={ce.Length} p80={p80.Length}");
        updMs.Add(Stopwatch.GetElapsedTime(t1).TotalMilliseconds);
        int take = last ? (me - ms) : nChunk * 8;
        outProbs.AddRange(p10.AsSpan(nc * 64, take * 8).ToArray());
        if (last) break;
        k++;
    }
    return outProbs.ToArray();
}

float[] RunOffline()
{
    // HF offline: centered pass over the whole utterance, T+1 frames, last one zeroed; padded to x8.
    var t0 = Stopwatch.GetTimestamp();
    var padded = Frontend.Padded(audio, rightPad: 256);
    var melAll = Frontend.Run(frontend, padded);
    int t = audio.Length / 160 + 1;
    int tp = (t + 7) / 8 * 8;
    var feats = new float[tp * 128];
    Array.Copy(melAll, feats, (t - 1) * 128);
    feMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    int ne = tp / 8;
    var state = new AoscState(fifoLen, updPeriod, silence);
    var outProbs = new List<float>();
    for (int s = 0; s < ne; s += chunk)
    {
        int e = Math.Min(s + chunk, ne), ceEnd = Math.Min(e + rc, ne);
        int nc = state.CachedFrames;
        var (ce, p10, p80) = RunStep(feats.AsSpan(s * 8 * 128, (ceEnd - s) * 8 * 128).ToArray(), (ceEnd - s) * 8, state, nc);
        var t1 = Stopwatch.GetTimestamp();
        state.Update(ce.AsSpan(0, (e - s) * 512), p80);
        updMs.Add(Stopwatch.GetElapsedTime(t1).TotalMilliseconds);
        outProbs.AddRange(p10.AsSpan(nc * 64, (e - s) * 64).ToArray());
    }
    return outProbs.Take(t * 8).ToArray();
}

(float[] ce, float[] p10, float[] p80) RunStep(float[] feats, int melFrames, AoscState state, int nc)
{
    var cached = new float[nc * 512];
    state.CopyCached(cached);
    using var fIn = OrtValue.CreateTensorValueFromMemory(feats, [1, melFrames, 128]);
    // a zero-length managed buffer pins to a null pointer -> allocate the empty tensor natively instead
    using var cIn = nc > 0
        ? OrtValue.CreateTensorValueFromMemory(cached, [1, nc, 512])
        : OrtValue.CreateAllocatedTensorValue(OrtAllocator.DefaultInstance, TensorElementType.Float, [1, 0, 512]);
    if (Environment.GetEnvironmentVariable("DIAR_TRACE") == "1") Console.Error.WriteLine($"step nc={nc} mel={melFrames} feats={feats.Length}");
    var t0 = Stopwatch.GetTimestamp();
    using var outs = step.Run(Ort.Run, new Dictionary<string, OrtValue> { ["features"] = fIn, ["cache_embeds"] = cIn },
        ["chunk_embeds", "probs", "frame_probs"]);
    stepMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    stepCached.Add(nc);
    return (outs[0].GetTensorDataAsSpan<float>().ToArray(), outs[1].GetTensorDataAsSpan<float>().ToArray(),
        outs[2].GetTensorDataAsSpan<float>().ToArray());
}

static double Pct(double[] sorted, int p) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * p / 100.0))];

static string FindRepo()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d != null && !File.Exists(Path.Combine(d.FullName, "NemotronSpeech.slnx"))) d = d.Parent;
    return d?.FullName ?? Directory.GetCurrentDirectory();
}

namespace DiarBench
{
    static class Ort
    {
        // One long-lived RunOptions. Passing `new RunOptions()` inline lets the GC finalize it while the
        // native Run is still executing (no KeepAlive) -> use-after-free AV after a few hundred steps.
        public static readonly RunOptions Run = new();
    }

    static class Frontend
    {
        /// <summary>256 zeros + pre-emphasized audio (+ optional right zero pad, after pre-emphasis).</summary>
        public static float[] Padded(float[] audio, int rightPad)
        {
            var p = new float[256 + audio.Length + rightPad];
            p[256] = audio[0];
            for (int i = 1; i < audio.Length; i++) p[256 + i] = audio[i] - 0.97f * audio[i - 1];
            return p;
        }

        public static float[] Run(InferenceSession fe, float[] padded, int start = 0, int count = -1)
        {
            if (count < 0) count = padded.Length - start;
            var seg = new float[count];
            Array.Copy(padded, start, seg, 0, Math.Min(count, padded.Length - start));
            using var x = OrtValue.CreateTensorValueFromMemory(seg, [1, count]);
            using var o = fe.Run(Ort.Run, new Dictionary<string, OrtValue> { ["audio"] = x }, ["log_mel"]);
            return o[0].GetTensorDataAsSpan<float>().ToArray();
        }

        /// <summary>Computes mel frames [mel.Count, upTo) incrementally (only new frames each step).</summary>
        public static void Extend(InferenceSession fe, float[] padded, List<float[]> mel, int upTo, int audioLen)
        {
            int from = mel.Count;
            if (upTo <= from) return;
            // frames past the end of audio read zeros (last chunk): matches HF's last-chunk zero padding
            int need = (upTo - 1) * 160 + 512 - from * 160;
            var m = Run(fe, padded, from * 160, need);
            for (int i = 0; i < upTo - from; i++) mel.Add(m.AsSpan(i * 128, 128).ToArray());
        }
    }

    static class Wav
    {
        public static float[] ReadMono16k(string path)
        {
            using var br = new BinaryReader(File.OpenRead(path));
            br.ReadBytes(12);
            int channels = 1, rate = 0, bits = 16;
            while (br.BaseStream.Position < br.BaseStream.Length)
            {
                string id = new(br.ReadChars(4));
                int size = br.ReadInt32();
                if (id == "fmt ")
                {
                    br.ReadInt16(); channels = br.ReadInt16(); rate = br.ReadInt32(); br.ReadInt32(); br.ReadInt16(); bits = br.ReadInt16();
                    br.ReadBytes(size - 16);
                }
                else if (id == "data")
                {
                    if (rate != 16000 || bits != 16) throw new NotSupportedException($"{rate} Hz / {bits} bit");
                    int n = size / 2 / channels;
                    var a = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        float s = 0;
                        for (int c = 0; c < channels; c++) s += br.ReadInt16() / 32768f;
                        a[i] = s / channels;
                    }
                    return a;
                }
                else br.ReadBytes(size + (size & 1));
            }
            throw new InvalidDataException("no data chunk");
        }
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
        public string Get(string k, string def) => _d.TryGetValue(k, out var v) ? v : def;
        public string? GetOrNull(string k) => _d.TryGetValue(k, out var v) ? v : null;
    }
}
