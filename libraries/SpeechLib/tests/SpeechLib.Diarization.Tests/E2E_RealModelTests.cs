using System.Diagnostics;
using Xunit.Abstractions;

namespace SpeechLib.Diarization.Tests;

/// <summary>Skipped unless SPEECHLIB_DIAR_MODEL points at the model folder.</summary>
public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute()
    {
        if (RealModel.Folder == null || RealModel.Data == null)
            Skip = "Set SPEECHLIB_DIAR_MODEL (nemotron-3-diarization-onnx folder) and SPEECHLIB_DIAR_DATA (folder with testset/ and cs/) to run the real-model tests.";
    }
}

internal static class RealModel
{
    /// <summary>Folder with diar_step[.int8].onnx, diar_frontend.onnx, diar_config.json.</summary>
    public static string? Folder => Env("SPEECHLIB_DIAR_MODEL");

    /// <summary>Folder holding <c>testset/*.wav + *.rttm</c> and <c>cs/*.f32</c> DiarBench dumps (the spike's scratch folder).</summary>
    public static string? Data => Env("SPEECHLIB_DIAR_DATA");

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v && Directory.Exists(v) ? v : null;

    public static DiarizationModel Load(DiarizationPrecision precision, int threads = 4) =>
        DiarizationModel.Load(new DiarizationModelOptions { ModelFolder = Folder!, Precision = precision, Threads = threads });

    public static string Wav(string name) => Path.Combine(Data!, "testset", name + ".wav");

    public static float[] ReadF32(string path)
    {
        var raw = File.ReadAllBytes(path);
        var f = new float[raw.Length / 4];
        Buffer.BlockCopy(raw, 0, f, 0, raw.Length);
        return f;
    }

    public static double WorkingSetMb() => Process.GetCurrentProcess().WorkingSet64 / 1e6;
}

/// <summary>
/// Reproduces the spike's numbers (tools/converters/NemotronDiarization/README.md) with the production library.
/// One model at a time: the collection is serialized because RAM is shared.
/// </summary>
[Collection("RealModel")]
public sealed class E2E_RealModelTests(ITestOutputHelper output)
{
    private static readonly (string Name, string Dump)[] ParityCases =
    [
        ("offline", "conv10_en_4spk_offline_diar_step.onnx.f32"),
        ("low_latency", "conv10_en_4spk_low_latency_diar_step.onnx.f32"),
        ("eco_2s", "conv10_en_4spk_eco_2s_diar_step.onnx.f32"),
    ];

    private static DiarizationPreset Preset(string name) => name switch
    {
        "offline" => DiarizationPreset.Offline,
        "low_latency" => DiarizationPreset.LowLatency,
        "eco_2s" => DiarizationPreset.Eco2s,
        _ => throw new ArgumentException(name),
    };

    [RealModelFact]
    public void Fp32_ProbabilitiesMatchTheSpikeDump()
    {
        using var model = RealModel.Load(DiarizationPrecision.Fp32);
        foreach (var (name, dumpFile) in ParityCases)
        {
            string dump = Path.Combine(RealModel.Data!, "cs", dumpFile);
            if (!File.Exists(dump)) continue;
            using var off = new OfflineDiarizer(model, new OfflineDiarizerOptions { Preset = Preset(name), PostProcessing = PostProcessingOptions.Raw });
            var result = off.DiarizeWavFile(RealModel.Wav("conv10_en_4spk"));
            var ours = result.Timeline!.ToMatrix();
            var reference = RealModel.ReadF32(dump);
            // the offline dump also holds HF's artificial trailing zero frame
            Assert.True(reference.Length >= ours.Length && reference.Length - ours.Length <= 8, $"{name}: {ours.Length / 8} vs {reference.Length / 8} frames");
            double max = 0;
            int flips = 0;
            for (int i = 0; i < ours.Length; i++)
            {
                max = Math.Max(max, Math.Abs(ours[i] - reference[i]));
                if ((ours[i] >= 0.5f) != (reference[i] >= 0.5f)) flips++;
            }
            output.WriteLine($"{name}: max |dp| = {max:E2}, flipped decisions = {flips}");
            Assert.True(max < 1e-4, $"{name}: max |dp| {max}");
            Assert.Equal(0, flips);
        }
    }

    [RealModelFact]
    public void Int8_LiveFeedingInSmallBlocksGivesTheOfflineWrapperResult()
    {
        using var model = RealModel.Load(DiarizationPrecision.Int8);
        string wav = RealModel.Wav("conv01_ru_2spk");
        using var off = new OfflineDiarizer(model, new OfflineDiarizerOptions { Preset = DiarizationPreset.LowLatency, PostProcessing = PostProcessingOptions.Raw });
        var whole = off.DiarizeWavFile(wav).Timeline!.ToMatrix();

        using var stream = new StreamingDiarizer(model, new StreamingDiarizerOptions { Preset = DiarizationPreset.LowLatency, PostProcessing = PostProcessingOptions.Raw });
        var audio = ReadAll(wav);
        for (int i = 0; i < audio.Length; i += 1600)    // 100 ms microphone-sized blocks
            stream.Feed(audio.AsSpan(i, Math.Min(1600, audio.Length - i)));
        stream.Flush();
        Assert.Equal(whole, stream.Timeline.ToMatrix());
    }

    [RealModelFact]
    public void Int8_DerOnSyntheticConversations_MatchesTheSpikeTable()
    {
        // Spike README, synthetic CV17 set, pyannote collar 0.25, raw 0.5 threshold, int8, all 12 files, reference-seconds weighted:
        // offline 10.82, low_latency 10.51, eco_2s 9.65. The production library reproduced 10.83 / - / 9.66 on all 12 files
        // (tools/DiarizationEval). This test runs a 3-file subset (en 2spk, en 4spk, ru 2spk) with the means measured there.
        string[] files = ["conv00_en_2spk", "conv04_en_4spk", "conv07_ru_2spk"];
        var expectedMean = new Dictionary<DiarizationPreset, double>
        {
            [DiarizationPreset.Offline] = 10.04,
            [DiarizationPreset.LowLatency] = 10.03,
            [DiarizationPreset.Eco2s] = 9.87,
        };
        using var model = RealModel.Load(DiarizationPrecision.Int8);
        foreach (var (preset, expected) in expectedMean)
        {
            double sum = 0;
            foreach (var f in files)
            {
                using var off = new OfflineDiarizer(model, new OfflineDiarizerOptions { Preset = preset, PostProcessing = PostProcessingOptions.Raw });
                var r = off.DiarizeWavFile(RealModel.Wav(f));
                var reference = DerScorer.ReadRttm(Path.ChangeExtension(RealModel.Wav(f), ".rttm"));
                var der = DerScorer.Score(reference, DerScorer.ToSegments(r.Turns), 0.25, r.DurationSeconds);
                output.WriteLine($"{preset} {f}: {der}  speakers={r.SpeakerCount}");
                sum += der.Der;
                // channels are numbered by first appearance (all 12 conversations of the set, offline int8)
                var firstSeen = r.Turns.OrderBy(t => t.Start).Select(t => t.Speaker).Distinct().ToList();
                Assert.Equal(firstSeen.Order().ToList(), firstSeen);
            }
            double mean = sum / files.Length * 100;
            output.WriteLine($"{preset} mean DER over {files.Length} files: {mean:F2}% (expected {expected:F2})");
            Assert.InRange(mean, expected - 1.0, expected + 1.0);
        }
    }

    [RealModelFact]
    public void Int8_RealTimeFactorAndMemory()
    {
        // Spike (Core Ultra 7 255HX, idle, 4 threads, 70.5 s file): low_latency sustained 0.19, eco_2s 0.048, offline 0.0067.
        // The PC is shared and CPU-bound runs are noisy, so the bounds here are loose; the numbers are in the test output.
        string wav = RealModel.Wav("conv10_en_4spk");
        long before = Process.GetCurrentProcess().WorkingSet64;
        using var model = RealModel.Load(DiarizationPrecision.Int8, threads: 4);
        output.WriteLine($"working set after load: {RealModel.WorkingSetMb():F0} MB (+{(Process.GetCurrentProcess().WorkingSet64 - before) / 1e6:F0})");
        foreach (var (preset, bound) in new[] { (DiarizationPreset.LowLatency, 1.0), (DiarizationPreset.Eco2s, 0.5), (DiarizationPreset.Offline, 0.3) })
        {
            using var off = new OfflineDiarizer(model, new OfflineDiarizerOptions { Preset = preset, PostProcessing = PostProcessingOptions.Raw });
            off.DiarizeWavFile(wav);                      // warm-up (kernels, arena)
            var r = off.DiarizeWavFile(wav);
            output.WriteLine($"{preset}: RTF {r.RealTimeFactor:F4} on {r.DurationSeconds:F1} s, working set {RealModel.WorkingSetMb():F0} MB");
            Assert.True(r.RealTimeFactor < bound, $"{preset} RTF {r.RealTimeFactor}");
        }
        double peak = Process.GetCurrentProcess().PeakWorkingSet64 / 1e6;
        output.WriteLine($"peak working set of the test process: {peak:F0} MB");
    }

    private static float[] ReadAll(string wav)
    {
        using var s = WavStream.Open(wav);
        var all = new List<float>();
        foreach (var b in s.ReadBlocks(16000)) all.AddRange(b.Span);
        return all.ToArray();
    }
}
