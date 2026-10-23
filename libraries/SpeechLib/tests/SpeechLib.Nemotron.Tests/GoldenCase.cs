using System.Text.Json;
using SpeechLib.Nemotron;

namespace SpeechLib.Nemotron.Tests;

/// <summary>What was read from the golden folders: the cases, and one message for every file that could not be read as a golden.</summary>
internal sealed record GoldenLoad(IReadOnlyList<GoldenCase> Cases, IReadOnlyList<string> Problems);

/// <summary>One chunk of a golden: the tokens GenAI emitted for it, or the fact that it was dropped.</summary>
/// <param name="Index">Chunk index on the stream grid; -1 for the chunk produced by <c>Flush()</c>.</param>
/// <param name="Processed">False when GenAI's processor returned nothing for the chunk (dropped by its VAD, or the partial last chunk before the flush).</param>
/// <param name="Ids">Token ids in emission order.</param>
internal sealed record GoldenChunk(int Index, bool Processed, int[] Ids);

/// <summary>
/// A recorded run of ORT GenAI 0.16.0 (the oracle): one clip through one model variant, with the token ids of every
/// chunk. The parity tests replay the same audio through <see cref="NemotronRnntRecognizer"/> and compare.
/// </summary>
/// <remarks>
/// JSON file, one per variant and clip, read (recursively) from <c>Data/golden</c> of the test project (a small set that
/// always runs), from <c>Data/golden-full</c> when <c>NEMOTRON_FULL_GOLDEN=1</c> (the complete oracle, tracked in the
/// repository: 210 clip x variant runs and 48 VAD runs) and from <c>NEMOTRON_GOLDEN_DIR</c>, which can point at any other
/// folder of the same shape. The large set has per-variant folders, <c>audioPath</c> as <c>TestAudio/&lt;relative&gt;</c> or
/// <c>golden/&lt;file&gt;</c>, entries carrying a <c>chunk</c> index, and <c>vad/&lt;variant&gt;/&lt;scenario&gt;.feed&lt;N&gt;.json</c>
/// files with <c>chunkCalls</c> / <c>flush</c>; all three shapes are understood. Every JSON file in these folders is a
/// golden except the manifests (<c>clips.json</c>, <c>index.json</c>, <c>vad_scenarios.json</c>) and everything under
/// <c>checks/</c>; a file that cannot be read as a golden is a <see cref="GoldenLoad.Problems">problem</see> and fails
/// the tests instead of vanishing from the suite. Members of the plain shape (all but <c>chunks</c> optional when they can be derived):
/// <code>
/// {
///   "variant": "int4-c056",            // int4|fp32 - c056|c112; derived from "model" (a path) or from the file name
///   "clip": "en0001",                  // derived from the file name oracle_int4c056_en0001.json
///   "audio": "cv17/en/0001.wav",       // under a Test-Audio root, NEMOTRON_GOLDEN_AUDIO or next to the golden (.wav or raw float32 .f32)
///   "scenario": [ {"clip":"cv17/en/0001.wav"}, {"silenceSeconds":10,"sigma":0.002,"seed":7} ],   // instead of "audio": parts concatenated
///   "chunkSamples": 8960, "langId": "0", "vad": false, "threads": 4, "seconds": 1.47,   // seconds = GenAI wall time
///   "text": " Joe Keaton ...", "tokenCount": 33,
///   "chunks": [ {"feed": 0, "processed": true, "ids": []}, ..., {"feed": "flush", "processed": true, "ids": []} ]
/// }
/// </code>
/// The audio was fed in pieces of <c>chunkSamples</c> (entry <c>feed: i</c> = chunk <c>i</c>; an entry may say
/// <c>"chunk": i</c> instead), then flushed. A <c>processed: false</c> entry before the flush is either a dropped
/// chunk (VAD goldens) or the partial last piece.
/// </remarks>
internal sealed class GoldenCase
{
    private GoldenCase()
    {
    }

    public required string FilePath { get; init; }
    public required string Name { get; init; }
    public required string Variant { get; init; }
    public required string Clip { get; init; }
    public string? Audio { get; init; }
    public JsonElement? Scenario { get; init; }
    public int ChunkSamples { get; init; }
    public string? LangId { get; init; }
    public bool Vad { get; init; }
    public int Threads { get; init; }

    /// <summary>Samples per <c>ProcessAudio</c> call of the recorded run; 0 = chunk aligned.</summary>
    public int FeedSamples { get; init; }
    public double? Seconds { get; init; }
    public string Text { get; init; } = "";
    public int TokenCount { get; init; }
    public required IReadOnlyList<GoldenChunk> Chunks { get; init; }

    public override string ToString() => Name;

    // Files that describe the set instead of being a golden of it.
    private static readonly HashSet<string> Manifests = new(StringComparer.OrdinalIgnoreCase) { "clips.json", "index.json", "vad_scenarios.json" };

    /// <summary>True when <c>NEMOTRON_FULL_GOLDEN</c> asks for the complete oracle in <c>Data/golden-full</c> (about 25 minutes more).</summary>
    public static bool FullSetRequested => IsTruthy(Environment.GetEnvironmentVariable("NEMOTRON_FULL_GOLDEN"));

    /// <summary>The switch values that count as "on" for an opt-in environment variable.</summary>
    internal static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the environment asks for more than the small repository set (an opt-in that must then be runnable).</summary>
    public static bool ExtraSetRequested =>
        FullSetRequested || Environment.GetEnvironmentVariable("NEMOTRON_GOLDEN_DIR") is { Length: > 0 };

    /// <summary>The existing golden folders in priority order: <c>NEMOTRON_GOLDEN_DIR</c>, the full set when requested, the repository set.</summary>
    public static IEnumerable<string> Directories()
    {
        foreach (var directory in Candidates(new List<string>()))
            yield return directory;
    }

    private static IEnumerable<string> Candidates(List<string> problems)
    {
        if (Environment.GetEnvironmentVariable("NEMOTRON_GOLDEN_DIR") is { Length: > 0 } custom)
        {
            if (Directory.Exists(custom))
                yield return custom;
            else
                problems.Add($"NEMOTRON_GOLDEN_DIR points at a folder that does not exist: {custom}");
        }
        if (FullSetRequested)
        {
            var full = NemotronTestEnvironment.DataPath("golden-full");
            if (Directory.Exists(full))
                yield return full;
            else
                problems.Add($"NEMOTRON_FULL_GOLDEN is set but {full} does not exist (it is copied from Data/golden-full of the test project).");
        }
        var data = NemotronTestEnvironment.DataPath("golden");
        if (Directory.Exists(data))
            yield return data;
    }

    private static readonly Lazy<GoldenLoad> Default = new(() =>
    {
        var problems = new List<string>();
        var directories = Candidates(problems).ToList();
        var load = LoadFrom(directories);
        return new GoldenLoad(load.Cases, [.. problems, .. load.Problems]);
    });

    /// <summary>Every golden of the configured folders, read once (see <see cref="Directories"/>).</summary>
    public static GoldenLoad LoadDefault() => Default.Value;

    /// <summary>Every golden that could be read; a name found twice resolves to the first directory (the environment's wins over the repository's).</summary>
    public static IReadOnlyList<GoldenCase> LoadAll() => Default.Value.Cases;

    /// <summary>Reads every golden under the folders; whatever cannot be read as a golden is returned as a problem, never dropped silently.</summary>
    public static GoldenLoad LoadFrom(IEnumerable<string> directories)
    {
        var cases = new List<GoldenCase>();
        var problems = new List<string>();
        var seen = new HashSet<string>();
        foreach (var directory in directories)
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                         .Where(f => !f.Replace((char)92, '/').Contains("/checks/") && !Manifests.Contains(Path.GetFileName(f)))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var parsed = TryParse(file, out var problem);
                if (parsed is null)
                    problems.Add($"{Path.GetRelativePath(directory, file)}: {problem}");
                else if (seen.Add(parsed.Name))
                    cases.Add(parsed);
            }
        return new GoldenLoad(cases, problems);
    }

    /// <summary>The cases of the main parity run (<paramref name="vad"/> false) or of the VAD run, with model and audio present.</summary>
    public static IEnumerable<GoldenCase> Enumerate(bool vad) =>
        LoadAll().Where(c => c.Vad == vad && c.WhyNotRunnable() is null);

    /// <summary>
    /// Theory rows of every case of the kind, runnable or not: variant and case name (strings, so every row is reported
    /// on its own). A case whose model or audio is missing becomes a skipped row with the reason
    /// (<see cref="NemotronGoldenTheoryDiscoverer"/>); a golden file that cannot be read fails the discovery.
    /// </summary>
    public static IEnumerable<object[]> Rows(bool vad)
    {
        var load = LoadDefault();
        if (load.Problems.Count > 0)
            throw new InvalidDataException("Golden files that cannot be read: " + string.Join("; ", load.Problems));
        return load.Cases.Where(c => c.Vad == vad).Select(c => new object[] { c.Variant, c.Name });
    }

    public static IEnumerable<object[]> ParityRows() => Rows(vad: false);

    public static IEnumerable<object[]> VadRows() => Rows(vad: true);

    public static GoldenCase Get(string name) =>
        Find(name) ?? throw new InvalidOperationException($"Golden case {name} not found.");

    public static GoldenCase? Find(string name) => LoadAll().FirstOrDefault(c => c.Name == name);

    public bool HasAudio() => MissingAudio() is null;

    // The first clip this golden needs that no audio root has, or null.
    private string? MissingAudio()
    {
        if (Scenario is { } scenario)
        {
            foreach (var part in scenario.EnumerateArray())
                if (part.TryGetProperty("clip", out var clip) && clip.GetString() is { } path && NemotronTestEnvironment.FindAudio(path) is null)
                    return path;
            return null;
        }
        return Audio is not null && NemotronTestEnvironment.FindAudio(Audio) is null ? Audio : null;
    }

    /// <summary>Why this case cannot run here (no model folder of its variant, no audio), or null when it can.</summary>
    public string? WhyNotRunnable()
    {
        if (NemotronTestEnvironment.LocateModel(Variant) is null)
            return $"model folder '{Variant}' not found (NEMOTRON_MODEL_DIR_{Variant.ToUpperInvariant().Replace('-', '_')}, or models/asr of the repository)";
        if (MissingAudio() is { } audio)
            return $"audio '{audio}' not found (the Common Voice clips come from SPEECHLIB_TEST_AUDIO or Test-Audio, the generated ones from NEMOTRON_GOLDEN_AUDIO: tools/nemotron-golden/README.md)";
        return null;
    }

    public float[] LoadAudio() =>
        Scenario is { } scenario ? SyntheticAudio.Build(scenario) : NemotronTestEnvironment.LoadAudioRelative(Audio!);

    /// <summary>Index of the flush chunk on the grid: the number of chunk-sized pieces that were fed.</summary>
    public int FlushChunkIndex(int audioSamples) => audioSamples / ChunkSamples;

    // ── parsing ─────────────────────────────────────────────────────────

    private static GoldenCase? TryParse(string file, out string problem)
    {
        problem = "";
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = "the JSON root is not an object";
                return null;
            }

            var isCalls = root.TryGetProperty("chunkCalls", out var callsElement) && callsElement.ValueKind == JsonValueKind.Array;
            if (!isCalls && !(root.TryGetProperty("chunks", out var probe) && probe.ValueKind == JsonValueKind.Array))
            {
                problem = "neither a \"chunks\" nor a \"chunkCalls\" array (a manifest? then add it to the ignored names)";
                return null;
            }

            var fileName = Path.GetFileNameWithoutExtension(file);
            var (fileVariant, fileClip) = SplitFileName(fileName);
            var variant = Str(root, "variant") ?? VariantFromModelPath(Str(root, "model")) ?? fileVariant;
            var clip = Str(root, "clip") ?? Str(root, "scenario") ?? fileClip;
            if (variant is null || clip is null)
            {
                problem = "no variant or clip: set \"variant\" and \"clip\" or name the file <variant>_<clip>.json";
                return null;
            }

            var chunkSamples = root.TryGetProperty("chunkSamples", out var cs) ? cs.GetInt32() : variant.EndsWith("c112") ? 17920 : 8960;
            var feedSamples = root.TryGetProperty("feedSamplesPerCall", out var fs) ? fs.GetInt32() : 0;
            var chunks = isCalls ? ParseCalls(root, callsElement) : ParseChunks(root.GetProperty("chunks"));
            var name = isCalls ? $"{variant}_{clip}.feed{feedSamples}" : $"{variant}_{clip}";

            return new GoldenCase
            {
                FilePath = file,
                Name = name,
                Variant = variant,
                Clip = clip,
                Audio = Str(root, "audio") ?? StripAudioRoot(Str(root, "audioPath")) ?? InferAudio(clip),
                Scenario = root.TryGetProperty("scenario", out var scenario) && scenario.ValueKind == JsonValueKind.Array ? scenario.Clone() : null,
                ChunkSamples = chunkSamples,
                FeedSamples = feedSamples,
                LangId = root.TryGetProperty("langId", out var lang) && lang.ValueKind == JsonValueKind.String ? lang.GetString() : null,
                Vad = isCalls || (root.TryGetProperty("vad", out var vad) && vad.ValueKind == JsonValueKind.True),
                Threads = root.TryGetProperty("threads", out var threads) ? threads.GetInt32() : 0,
                Seconds = root.TryGetProperty("seconds", out var seconds) ? seconds.GetDouble() : null,
                Text = Str(root, "text") ?? "",
                TokenCount = root.TryGetProperty("tokenCount", out var tc) ? tc.GetInt32() : chunks.Sum(c => c.Ids.Length),
                Chunks = chunks,
            };
        }
        catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            problem = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    // "chunks": one entry per Process() call, then the flush entry. Entry shapes seen: {feed: i} (the call index is the
    // chunk index), {feed: i, chunk: k}, and {feed, chunk: -1, processed: false} for the partial last piece.
    private static List<GoldenChunk> ParseChunks(JsonElement chunksElement)
    {
        var chunks = new List<GoldenChunk>();
        var position = 0;
        foreach (var entry in chunksElement.EnumerateArray())
        {
            var ids = entry.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            var processed = entry.TryGetProperty("processed", out var p) ? p.GetBoolean() : true;
            var isFlush = entry.TryGetProperty("feed", out var feed) && feed.ValueKind == JsonValueKind.String;
            int index;
            if (isFlush)
                index = -1;
            else if (entry.TryGetProperty("chunk", out var chunk) && chunk.GetInt32() >= 0)
                index = chunk.GetInt32();
            else if (entry.TryGetProperty("chunk", out chunk))
                index = int.MaxValue;                                 // the partial last piece: no chunk of the grid yet
            else if (feed.ValueKind == JsonValueKind.Number)
                index = feed.GetInt32();
            else
                index = position;
            position = index == int.MaxValue ? position : index + 1;
            chunks.Add(new GoldenChunk(index, processed, ids));
        }
        return chunks;
    }

    // VAD scenario files: every Process() call that consumed a chunk (processed=false: dropped) plus the flush result.
    private static List<GoldenChunk> ParseCalls(JsonElement root, JsonElement calls)
    {
        var chunks = new List<GoldenChunk>();
        foreach (var call in calls.EnumerateArray())
            chunks.Add(new GoldenChunk(
                call.GetProperty("chunk").GetInt32(),
                call.GetProperty("processed").GetBoolean(),
                call.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32()).ToArray()));
        if (root.TryGetProperty("flush", out var flush) && flush.ValueKind == JsonValueKind.Object && flush.TryGetProperty("ids", out var flushIds))
            chunks.Add(new GoldenChunk(-1, true, flushIds.EnumerateArray().Select(e => e.GetInt32()).ToArray()));
        return chunks;
    }

    // "TestAudio/cv17/en/0001.wav" and "golden/gen-vad-a4-10s.wav" name a root and a path below it
    private static string? StripAudioRoot(string? audioPath)
    {
        if (audioPath is null)
            return null;
        foreach (var prefix in new[] { "TestAudio/", "golden/" })
            if (audioPath.StartsWith(prefix, StringComparison.Ordinal))
                return audioPath[prefix.Length..];
        return audioPath;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // "int4-c056_en0001" (ours) or "oracle_int4c056_en0001" (the raw oracle runner)
    private static (string? Variant, string? Clip) SplitFileName(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^(?:oracle_)?(int4|fp32)-?c(\d{3})_(.+)$");
        return match.Success ? ($"{match.Groups[1].Value}-c{match.Groups[2].Value}", match.Groups[3].Value) : (null, null);
    }

    private static string? VariantFromModelPath(string? model)
    {
        if (model is null)
            return null;
        var match = System.Text.RegularExpressions.Regex.Match(model, @"(int4|fp32)-c(\d{3})");
        return match.Success ? $"{match.Groups[1].Value}-c{match.Groups[2].Value}" : null;
    }

    private static string? InferAudio(string clip)
    {
        var match = System.Text.RegularExpressions.Regex.Match(clip, @"^(en|ru)(\d{4})$");
        return match.Success ? $"cv17/{match.Groups[1].Value}/{match.Groups[2].Value}.wav" : $"{clip}.f32";
    }
}

/// <summary>
/// Deterministic test audio: concatenated clips and room tone from a fixed-seed generator, so a golden can describe
/// its audio in a few lines and the GenAI oracle that recorded it can rebuild exactly the same samples.
/// </summary>
internal static class SyntheticAudio
{
    public const int SampleRate = 16000;

    /// <summary>Builds the audio of a golden's <c>scenario</c> array.</summary>
    public static float[] Build(JsonElement scenario)
    {
        var parts = new List<float[]>();
        foreach (var part in scenario.EnumerateArray())
        {
            if (part.TryGetProperty("clip", out var clip))
            {
                parts.Add(NemotronTestEnvironment.LoadAudioRelative(clip.GetString()!));
            }
            else
            {
                var seconds = part.GetProperty("silenceSeconds").GetDouble();
                var sigma = part.TryGetProperty("sigma", out var s) ? (float)s.GetDouble() : 0f;
                var seed = part.TryGetProperty("seed", out var sd) ? sd.GetUInt32() : 1u;
                parts.Add(RoomTone((int)Math.Round(seconds * SampleRate), sigma, seed));
            }
        }
        return Concat(parts);
    }

    /// <summary>Gaussian-ish room tone (sum of twelve uniform values from an xorshift32 stream), or digital silence for sigma 0.</summary>
    public static float[] RoomTone(int samples, float sigma, uint seed)
    {
        var tone = new float[samples];
        if (sigma == 0f)
            return tone;
        var state = seed * 2654435761u + 0x9E3779B9u;
        if (state == 0)
            state = 1;
        for (var i = 0; i < samples; i++)
        {
            var sum = 0f;
            for (var k = 0; k < 12; k++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                sum += (state >> 8) / 16777216f;
            }
            tone[i] = (sum - 6f) * sigma;
        }
        return tone;
    }

    public static float[] Concat(IEnumerable<float[]> parts)
    {
        var list = parts.ToList();
        var all = new float[list.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in list)
        {
            part.CopyTo(all, offset);
            offset += part.Length;
        }
        return all;
    }
}
