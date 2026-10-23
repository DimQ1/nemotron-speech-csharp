using System.Globalization;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace SpeechLib.Nemotron.Tests;

/// <summary>Runs the test only with the WebGPU prerequisites (see <see cref="WebGpuHardware"/>) and the fp32 model, with 6 GB of memory free for its two child processes.</summary>
public sealed class WebGpuDiscreteFp32FactAttribute : FactAttribute
{
    public WebGpuDiscreteFp32FactAttribute()
    {
        Skip = WebGpuHardware.SkipReason
            ?? (NemotronTestEnvironment.LocateModel("fp32-c056") is null ? "Nemotron model folder 'fp32-c056' not found." : null)
            ?? (WebGpuHardware.FreeMemoryBytes() is { } free && free < 6L * 1024 * 1024 * 1024
                ? $"only {free / (1024 * 1024 * 1024.0):F1} GB of memory is free; the fp32 model needs about 2.5 GB per process (6 GB required)."
                : null);
    }
}

/// <summary>
/// The real WebGPU execution provider on a discrete graphics card, end to end: <c>WerEval nemotron --streaming --ep webgpu:hp</c> against
/// <c>--ep cpu</c> on the same Common Voice clips, each in a CHILD process with a time limit, because a fault in the plugin or the driver
/// ends the process (fail-fast 0xC0000409, heap corruption 0xC0000374, access violation 0xC0000005, a hang) and must fail the test with the
/// exit code instead of killing the test host. Nothing in this class touches the plugin in the test process. Skipped, with the reason,
/// without the plugin or a discrete card.
/// </summary>
/// <remarks>
/// The assertion is parity of the text, not bit parity of the encoder: the GPU does not reproduce the CPU's float arithmetic. Measured
/// (INT4 c056, CV17 en+ru, 500 clips): 15 to 18 percent of the clips differ by a word or a mark, +0.49 pp WER (c112: +0.07 pp); fp32 gives the
/// same text on 200 of 200 clips. So the INT4 check allows ±0.6 pp, widened to two words on a small clip set (0.6 pp is only one word and a half
/// on 24 clips, below the clip-to-clip noise), and the fp32 check demands the same text everywhere.
/// </remarks>
[Collection(NemotronModelCollection.Name)]
public sealed class E2E_NemotronWebGpuTests : IDisposable
{
    private const int ClipsPerLanguage = 12;
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(6);

    private readonly ITestOutputHelper _output;
    private readonly string _work = Path.Combine(Path.GetTempPath(), "nemotron-webgpu-e2e-" + Guid.NewGuid().ToString("N"));

    public E2E_NemotronWebGpuTests(NemotronModelCache models, ITestOutputHelper output)
    {
        _output = output;
        models.ReleaseAll();
    }

    public void Dispose()
    {
        if (Directory.Exists(_work))
            Directory.Delete(_work, recursive: true);
    }

    private sealed record Run(string Provider, int Words, int Errors, Dictionary<string, string> Hypotheses, ChildResult Child)
    {
        public double Wer => 100.0 * Errors / Words;
    }

    /// <summary>Copies the first clips of CV17 English and Russian (wav + reference) into a folder of their own: the same set for every run.</summary>
    private string PrepareClips()
    {
        var root = Path.Combine(_work, "clips");
        var english = Path.GetDirectoryName(NemotronTestEnvironment.FindAudio("cv17/en/0001.wav"))!;
        foreach (var language in new[] { "en", "ru" })
        {
            var source = Path.Combine(Path.GetDirectoryName(english)!, language);
            var target = Directory.CreateDirectory(Path.Combine(root, language)).FullName;
            foreach (var wav in Directory.GetFiles(source, "*.wav").OrderBy(p => p, StringComparer.Ordinal)
                         .Where(w => File.Exists(Path.ChangeExtension(w, ".txt"))).Take(ClipsPerLanguage))
            {
                File.Copy(wav, Path.Combine(target, Path.GetFileName(wav)));
                File.Copy(Path.ChangeExtension(wav, ".txt"), Path.Combine(target, Path.GetFileName(Path.ChangeExtension(wav, ".txt"))));
            }
        }
        return root;
    }

    private Run WerEval(string model, string clips, string provider, string label)
    {
        var markerFolder = Path.Combine(_work, "marker-" + label);   // a crash leaves its marker here, not in the user's folder
        var child = ChildProcess.Run("dotnet",
            [WebGpuHardware.WerEvalPath()!, "nemotron", model, clips, "--streaming", "--verbose", "--ep", provider],
            Limit,
            new Dictionary<string, string> { ["SPEECHLIB_WEBGPU_STATE_DIR"] = markerFolder });
        File.WriteAllText(Path.Combine(_work, $"{label}.log"), child.Output + "\n--- stderr ---\n" + child.Error);

        Assert.True(child.Succeeded, $"WerEval --ep {provider} ({label}) ended with {child.Describe()}\n{child.Tail()}");

        var total = Regex.Match(child.Output, @"^TOTAL\s+(\d+)\s+(\d+)\s+(\d+)\s", RegexOptions.Multiline);
        Assert.True(total.Success, "no TOTAL row in the WerEval output\n" + child.Tail());
        var hypotheses = Regex.Matches(child.Output, @"^\[(?<clip>[^\]]+)\] WER=.*\r?\n  REF: .*\r?\n  HYP: (?<hyp>.*)$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups["clip"].Value, m => m.Groups["hyp"].Value.TrimEnd('\r'));
        Assert.Equal(int.Parse(total.Groups[1].Value, CultureInfo.InvariantCulture), hypotheses.Count);

        var effective = Regex.Match(child.Output, @"^Nemotron execution provider: (\w+)", RegexOptions.Multiline).Groups[1].Value;
        var run = new Run(effective,
            int.Parse(total.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(total.Groups[3].Value, CultureInfo.InvariantCulture),
            hypotheses, child);
        _output.WriteLine($"{label}: provider {effective}, {hypotheses.Count} clips, WER {run.Wer:F2} % ({run.Errors} errors in {run.Words} words)");
        return run;
    }

    private static int DifferingClips(Run cpu, Run gpu) => cpu.Hypotheses.Count(kv => gpu.Hypotheses[kv.Key] != kv.Value);

    [WebGpuDiscreteFact]
    public void Int4_EncoderOnWebGpu_ShouldMatchTheCpuWer_WithoutACrash()
    {
        var model = NemotronTestEnvironment.LocateModel("int4-c056")!;
        var clips = PrepareClips();

        var cpu = WerEval(model, clips, "cpu", "int4-cpu");
        var gpu = WerEval(model, clips, "webgpu:hp", "int4-webgpu");

        Assert.Equal("cpu", cpu.Provider);
        Assert.Equal("webgpu", gpu.Provider);   // the encoder really ran on WebGPU: a silent fallback would make the comparison meaningless
        Assert.Equal(cpu.Words, gpu.Words);
        var delta = gpu.Wer - cpu.Wer;
        var differing = DifferingClips(cpu, gpu);
        _output.WriteLine($"WER cpu {cpu.Wer:F2} %, webgpu {gpu.Wer:F2} %, difference {delta:+0.00;-0.00} pp ({gpu.Errors - cpu.Errors:+0;-0;0} words); {differing} of {cpu.Hypotheses.Count} clips differ in text");

        var allowedWords = Math.Max(2, (int)Math.Floor(0.6 * cpu.Words / 100.0));
        Assert.True(Math.Abs(gpu.Errors - cpu.Errors) <= allowedWords,
            $"WER on WebGPU {gpu.Wer:F2} % differs from the CPU's {cpu.Wer:F2} % by {delta:+0.00;-0.00} pp ({gpu.Errors - cpu.Errors:+0;-0;0} words, {allowedWords} allowed)");
        Assert.True(differing <= cpu.Hypotheses.Count * 0.4,
            $"{differing} of {cpu.Hypotheses.Count} clips differ in text between WebGPU and the CPU (INT4 differs in about 15 to 18 percent)");
    }

    [WebGpuDiscreteFp32Fact]
    public void Fp32_EncoderOnWebGpu_ShouldGiveTheSameTextAsTheCpu()
    {
        var model = NemotronTestEnvironment.LocateModel("fp32-c056")!;
        var clips = PrepareClips();

        var cpu = WerEval(model, clips, "cpu", "fp32-cpu");
        var gpu = WerEval(model, clips, "webgpu:hp", "fp32-webgpu");

        Assert.Equal("cpu", cpu.Provider);
        Assert.Equal("webgpu", gpu.Provider);
        var differing = cpu.Hypotheses.Where(kv => gpu.Hypotheses[kv.Key] != kv.Value).Select(kv => $"{kv.Key}\n  cpu: {kv.Value}\n  gpu: {gpu.Hypotheses[kv.Key]}").ToList();
        Assert.True(differing.Count == 0, $"{differing.Count} of {cpu.Hypotheses.Count} clips differ between fp32 WebGPU and fp32 CPU:\n{string.Join('\n', differing)}");
    }

    [WebGpuDiscreteFact]
    public void LowPowerAndRawOptionRequests_ShouldFollowThePolicyInARealProcess()
    {
        var model = NemotronTestEnvironment.LocateModel("int4-c056")!;
        var clips = PrepareClips();
        Directory.Delete(Path.Combine(clips, "ru"), recursive: true);   // two quick runs: only the reported provider matters here

        var lowPower = WerEval(model, clips, "webgpu:lp", "lp");
        var indexAndOptions = WerEval(model, clips, "webgpu:0,layout=NHWC,capture=1", "raw-options");

        Assert.Equal("cpu", lowPower.Provider);          // the integrated GPU is slower than the CPU
        Assert.Equal("webgpu", indexAndOptions.Provider); // parsed, ignored, the discrete card with default options (capture=1 would fail, int64 crash)
        Assert.Contains("ignored for Nemotron", indexAndOptions.Child.Output);
    }
}
