#:property TargetFramework=net10.0
#:property PublishAot=false
// Deterministic generator of every synthetic input of the Nemotron golden set.
//
//   dotnet run gen_audio.cs -- <TestAudioRoot> <outDir>
//
// Output: <outDir>/<id>.wav (16 kHz mono PCM16) and <outDir>/generated_audio.json (id, samples, sha256 of the PCM data, recipe).
// Everything is integer arithmetic on int16 samples taken from the (read-only) Test-Audio folder, so the result is bit-exact on
// every platform. Room tone = Irwin-Hall approximation of white Gaussian noise (sum of 12 uniforms) from a xorshift32 stream,
// sigma = 66 LSB (= 0.002 full scale, -54 dBFS rms, the level A4's VAD experiment used with System.Random + Box-Muller).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

const int Rate = 16000;
const int RoomSigmaLsb = 66;

string testAudio = args[0];
string outDir = args[1];
Directory.CreateDirectory(outDir);

short[] Cv(string lang, int n) => ReadWav(Path.Combine(testAudio, "cv17", lang, $"{n:D4}.wav"));

var manifest = new List<object>();
var seeds = new Dictionary<string, uint>();

void Emit(string id, short[] pcm, string recipe)
{
    string file = id + ".wav";
    WriteWav(Path.Combine(outDir, file), pcm);
    manifest.Add(new
    {
        id,
        file,
        samples = pcm.Length,
        seconds = Math.Round(pcm.Length / (double)Rate, 3),
        sha256Pcm = Sha256Pcm(pcm),
        recipe,
    });
    Console.WriteLine($"{id,-34} {pcm.Length,9} samples  {pcm.Length / (double)Rate,7:F2} s");
}

// ---- room tone -------------------------------------------------------------------------------------------------------
// One xorshift32 stream per clip (seed documented in the recipe), consumed in order of generation.
uint state = 0;
void Seed(uint s) => state = s;
uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
short[] Tone(int n)
{
    var x = new short[n];
    for (int i = 0; i < n; i++)
    {
        long sum = 0;
        for (int k = 0; k < 12; k++) sum += Next() >> 16;
        sum -= 12 * 32768;                                   // mean 0, std 65536
        long v = (sum * RoomSigmaLsb + 32768) >> 16;          // std = 66 LSB
        x[i] = (short)Math.Clamp(v, short.MinValue, short.MaxValue);
    }
    return x;
}
short[] Cat(params short[][] parts)
{
    var r = new short[parts.Sum(p => p.Length)];
    int o = 0;
    foreach (var p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
    return r;
}
short[] Take(short[] x, int from, int count) => x.AsSpan(from, count).ToArray();
short[] Zeros(int n) => new short[n];

// ---- long concatenations with room-tone gaps (gap k = 1 + k % 3 seconds) -----------------------------------------------
short[] Concat(IEnumerable<short[]> clips, uint seed, out string gaps)
{
    Seed(seed);
    var parts = new List<short[]> { Tone(1 * Rate) };           // 1 s lead
    var gapList = new List<int>();
    int k = 0;
    foreach (var c in clips)
    {
        if (k > 0) { int g = 1 + (k - 1) % 3; gapList.Add(g); parts.Add(Tone(g * Rate)); }
        parts.Add(c);
        k++;
    }
    parts.Add(Tone(2 * Rate));                                   // 2 s tail
    gaps = string.Join(",", gapList);
    return Cat(parts.ToArray());
}

var en12 = Enumerable.Range(1, 12).Select(i => Cv("en", i)).ToList();
var ru12 = Enumerable.Range(1, 12).Select(i => Cv("ru", i)).ToList();
Emit("gen-concat-en-gaps", Concat(en12, 101, out var g1),
    $"seed 101: 1 s room tone, cv17/en 0001..0012 in order separated by room tone gaps of {g1} s, 2 s room tone tail");
Emit("gen-concat-ru-gaps", Concat(ru12, 102, out var g2),
    $"seed 102: 1 s room tone, cv17/ru 0001..0012 in order separated by room tone gaps of {g2} s, 2 s room tone tail");
var mix = new List<short[]>();
for (int i = 1; i <= 5; i++) { mix.Add(Cv("en", i)); mix.Add(Cv("ru", i)); }
Emit("gen-concat-mix-auto", Concat(mix, 103, out var g3),
    $"seed 103: 1 s room tone, cv17 en0001 ru0001 en0002 ru0002 ... en0005 ru0005 separated by room tone gaps of {g3} s, 2 s tail (use lang_id 101 = auto)");

// ---- edge lengths --------------------------------------------------------------------------------------------------------
var en1 = Cv("en", 1);
var ru1 = Cv("ru", 1);
var en2 = Cv("en", 2);
// highest-energy 4800 samples (0.3 s) of en0001, 160-sample search step, first maximum wins
int bestOff = 0; long bestE = -1;
for (int off = 0; off + 4800 <= en1.Length; off += 160)
{
    long e = 0; for (int i = 0; i < 4800; i++) e += (long)en1[off + i] * en1[off + i];
    if (e > bestE) { bestE = e; bestOff = off; }
}
Emit("gen-short-0p3s-en", Take(en1, bestOff, 4800), $"cv17/en/0001 samples [{bestOff}, {bestOff + 4800}) = the highest-energy 0.3 s window (shorter than one chunk of either variant)");
Emit("gen-exact-8960-en", Take(en1, 0, 8960), "cv17/en/0001 first 8960 samples = exactly one c056 chunk, half a c112 chunk");
Emit("gen-exact-17920-en", Take(en2, 0, 17920), "cv17/en/0002 first 17920 samples = exactly one c112 chunk (two c056 chunks)");
Emit("gen-exact-53760-en", Take(en1, 0, 53760), "cv17/en/0001 first 53760 samples = 6 c056 chunks = 3 c112 chunks exactly (no flush chunk)");
Emit("gen-exact-53761-en", Take(en1, 0, 53761), "cv17/en/0001 first 53761 samples = the above plus ONE sample (flush pads 8959 zeros)");
Emit("gen-exact-53760-ru", Take(ru1, 0, 53760), "cv17/ru/0001 first 53760 samples = 6 c056 chunks = 3 c112 chunks exactly");
Emit("gen-exact-53759-en", Take(en1, 0, 53759), "cv17/en/0001 first 53759 samples = one sample short of 6 c056 chunks (flush pads 1 zero)");

// ---- silence and quiet ------------------------------------------------------------------------------------------------------
Seed(201);
Emit("gen-silence-room-5s", Tone(5 * Rate), "seed 201: 5 s of room tone only (white noise sigma 66 LSB = -54 dBFS rms)");
Emit("gen-silence-zeros-2s", Zeros(2 * Rate), "2 s of exact digital zeros (log(0 + eps) path)");
short[] Quiet(short[] x) => x.Select(s => (short)Math.Round(s / 32.0, MidpointRounding.AwayFromZero)).ToArray();
Emit("gen-quiet-30dB-en0001", Quiet(en1), "cv17/en/0001 attenuated by 1/32 (-30.1 dB), rounded half away from zero");
Emit("gen-quiet-30dB-ru0001", Quiet(ru1), "cv17/ru/0001 attenuated by 1/32 (-30.1 dB), rounded half away from zero");

// ---- VAD scenarios (native use_vad), audio recipe of A4's exp1 ------------------------------------------------------------------
var enB = Cv("en", 5);
short[] VadClip(uint seed, int gapSamples, int padTo)
{
    Seed(seed);
    var lead = Tone(1 * Rate);
    var pre = Cat(lead, en1);
    int pad = padTo > 0 ? (padTo - pre.Length % padTo) % padTo : 0;
    return Cat(pre, Tone(pad), Tone(gapSamples), enB, Tone(2 * Rate));
}
Emit("gen-vad-a4-10s", VadClip(301, 10 * Rate, 0),
    "seed 301: 1 s room tone, cv17/en/0001, 10 s room tone, cv17/en/0005, 2 s room tone (A4 exp1 recipe; native VAD drops the 6th..13th silent chunk on c056)");
Emit("gen-vad-pause-2s", VadClip(302, 2 * Rate, 0),
    "seed 302: 1 s room tone, cv17/en/0001, 2 s room tone, cv17/en/0005, 2 s room tone (no chunk dropped expected: needs 6 silent c056 chunks = 3.36 s)");
foreach (int chunks in new[] { 4, 5, 6, 7 })
    Emit($"gen-vad-gap-{chunks}chunks", VadClip((uint)(310 + chunks), chunks * 8960, 17920),
        $"seed {310 + chunks}: 1 s room tone + cv17/en/0001 padded with room tone to a multiple of 17920 samples, then exactly {chunks} x 8960 samples of room tone (chunk aligned for c056 and, when even, for c112), cv17/en/0005, 2 s room tone");

File.WriteAllText(Path.Combine(outDir, "generated_audio.json"),
    JsonSerializer.Serialize(new { rate = Rate, roomSigmaLsb = RoomSigmaLsb, clips = manifest }, new JsonSerializerOptions { WriteIndented = true }));

// ---- helpers -------------------------------------------------------------------------------------------------------------------
static string Sha256Pcm(short[] pcm)
{
    var b = new byte[pcm.Length * 2];
    Buffer.BlockCopy(pcm, 0, b, 0, b.Length);   // little endian on every supported platform
    return Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
}

static short[] ReadWav(string path)
{
    var b = File.ReadAllBytes(path);
    int pos = 12;
    int bits = 16, fmt = 1;
    while (pos + 8 <= b.Length)
    {
        string id = Encoding.ASCII.GetString(b, pos, 4);
        int size = BitConverter.ToInt32(b, pos + 4);
        if (id == "fmt ") { fmt = BitConverter.ToUInt16(b, pos + 8); bits = BitConverter.ToInt16(b, pos + 22); }
        if (id == "data")
        {
            if (fmt != 1 || bits != 16) throw new InvalidDataException($"{path}: expected PCM16");
            int n = Math.Min(size, b.Length - pos - 8) / 2;
            var x = new short[n];
            Buffer.BlockCopy(b, pos + 8, x, 0, n * 2);
            return x;
        }
        pos += 8 + size + (size & 1);
    }
    throw new InvalidDataException(path);
}

static void WriteWav(string path, short[] pcm)
{
    using var w = new BinaryWriter(File.Create(path));
    int dataBytes = pcm.Length * 2;
    w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + dataBytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
    w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
    w.Write(Encoding.ASCII.GetBytes("data")); w.Write(dataBytes);
    var bytes = new byte[dataBytes];
    Buffer.BlockCopy(pcm, 0, bytes, 0, dataBytes);
    w.Write(bytes);
}
