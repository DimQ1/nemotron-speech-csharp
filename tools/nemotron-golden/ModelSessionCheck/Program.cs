// Fidelity check of the golden harness: runs clips through the repository's real ModelSession (production class, default thread count,
// repetition_penalty 1.1 as RecognizerFactory passes it, language resolved by LanguageMapper) and compares with the golden JSON:
//   pass 1 (aligned, chunk_samples per call, ProcessAudio -> SetInputs -> DecodeTokens): text of every chunk and the token count of the run
//   pass 2 (ragged, 3200 samples per call through the IStreamingSpeechRecognizer interface, as live capture feeds it): final text
// usage: ModelSessionCheck <modelDir> <goldenDir> <TestAudioRoot> <goldenAudioDir> <clipId>[,<clipId>...] [<threads>]
using System.Text;
using System.Text.Json;
using SpeechLib;

string modelDir = args[0], goldenDir = args[1], testAudio = args[2], goldenAudio = args[3];
var ids = args[4].Split(',');
int? threads = args.Length > 5 && int.Parse(args[5]) > 0 ? int.Parse(args[5]) : null;
var clips = JsonDocument.Parse(File.ReadAllText(Path.Combine(goldenDir, "..", "clips.json"))).RootElement.GetProperty("clips").EnumerateArray().ToDictionary(c => c.GetProperty("id").GetString()!);

int bad = 0;
foreach (var id in ids)
{
    var clip = clips[id];
    var golden = JsonDocument.Parse(File.ReadAllText(Path.Combine(goldenDir, id + ".json"))).RootElement;
    string root = clip.GetProperty("root").GetString() == "TestAudio" ? testAudio : goldenAudio;
    var audio = ReadWav(Path.Combine(root, clip.GetProperty("path").GetString()!));
    string? lang = clip.GetProperty("lang").GetString();
    var args2 = new GeneratorParamsArgs { do_sample = false, repetition_penalty = 1.1 };

    // pass 1
    var chunkTexts = new List<string>();
    int tokens = 0;
    using (var session = new ModelSession(modelDir, "cpu", lang, useVad: false, args2, threads))
    {
        int chunk = session.ChunkSamples;
        for (int i = 0; i < audio.Length; i += chunk)
        {
            var part = audio.AsSpan(i, Math.Min(chunk, audio.Length - i)).ToArray();
            using var t = session.ProcessAudio(part);
            if (t is null) continue;
            session.SetInputs(t);
            var r = session.DecodeTokens();
            chunkTexts.Add(r.Text); tokens += r.TokenCount;
        }
        using var f = session.Flush();
        if (f is not null) { session.SetInputs(f); var r = session.DecodeTokens(); chunkTexts.Add(r.Text); tokens += r.TokenCount; }
    }
    var goldenTexts = golden.GetProperty("chunks").EnumerateArray().Where(c => c.GetProperty("processed").GetBoolean())
        .Select(c => string.Concat(c.GetProperty("pieces").EnumerateArray().Select(p => p.GetString()))).ToList();
    bool p1 = chunkTexts.SequenceEqual(goldenTexts) && tokens == golden.GetProperty("tokenCount").GetInt32();

    // pass 2
    var sb = new StringBuilder();
    using (var rec = new ModelSession(modelDir, "cpu", lang, useVad: false, args2, threads))
    {
        IStreamingSpeechRecognizer r = rec;
        for (int i = 0; i < audio.Length; i += 3200)
            sb.Append(r.ProcessAudio(audio.AsSpan(i, Math.Min(3200, audio.Length - i)).ToArray()));
        sb.Append(r.Flush());
    }
    bool p2 = sb.ToString() == golden.GetProperty("text").GetString();
    if (!p1 || !p2) bad++;
    Console.WriteLine($"{id,-26} chunk-level={(p1 ? "identical" : "DIFF")} ragged-interface-text={(p2 ? "identical" : "DIFF")} tokens={tokens}");
    if (!p2) Console.WriteLine($"   ragged: [{sb}]\n   golden: [{golden.GetProperty("text").GetString()}]");
}
Console.WriteLine(bad == 0 ? "ALL IDENTICAL" : $"{bad} clip(s) differ");
return bad;

static float[] ReadWav(string path)
{
    var b = File.ReadAllBytes(path);
    int pos = 12, fmt = 1, bits = 16;
    while (pos + 8 <= b.Length)
    {
        string cid = Encoding.ASCII.GetString(b, pos, 4);
        int size = BitConverter.ToInt32(b, pos + 4);
        if (cid == "fmt ") { fmt = BitConverter.ToUInt16(b, pos + 8); bits = BitConverter.ToInt16(b, pos + 22); }
        if (cid == "data")
        {
            int bytes = Math.Min(size, b.Length - pos - 8);
            if (fmt == 1 && bits == 16) { var x = new float[bytes / 2]; for (int i = 0; i < x.Length; i++) x[i] = BitConverter.ToInt16(b, pos + 8 + 2 * i) / 32768f; return x; }
            if (fmt == 3) { var x = new float[bytes / 4]; Buffer.BlockCopy(b, pos + 8, x, 0, x.Length * 4); return x; }
            throw new InvalidDataException(path);
        }
        pos += 8 + size + (size & 1);
    }
    throw new InvalidDataException(path);
}
