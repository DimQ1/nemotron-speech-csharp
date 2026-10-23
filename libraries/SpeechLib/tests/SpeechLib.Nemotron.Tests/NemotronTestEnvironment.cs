using System.Buffers.Binary;
using SpeechLib.Nemotron;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// Where the gitignored models and test audio are found. Every lookup tries, in this order, an environment
/// variable, the repository root (walking up from the test binaries to <c>NemotronSpeech.slnx</c>) and, when that
/// root is a git worktree without the files, the main checkout it was created from (resolved through the git
/// common dir of the worktree's <c>.git</c> file).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>NEMOTRON_MODEL_DIR_INT4_C056</c> (and <c>_INT4_C112</c>, <c>_FP32_C056</c>, <c>_FP32_C112</c>): the model folder of that variant.</item>
/// <item><c>NEMOTRON_MODEL_DIR</c>: one model folder, used for the variant named by <c>NEMOTRON_MODEL_VARIANT</c> (default <c>int4-c056</c>).</item>
/// <item><c>NEMOTRON_MODELS_ROOT</c>: a folder that contains <c>asr/&lt;model folder&gt;</c> or the model folders themselves.</item>
/// <item><c>SPEECHLIB_TEST_AUDIO</c>: the Test-Audio folder (with <c>cv17/en/0001.wav</c> ...), shared with the Parakeet tests.</item>
/// <item><c>NEMOTRON_FULL_GOLDEN=1</c>: also replay the complete oracle in <c>Data/golden-full</c> (210 clip x variant runs and 48 VAD runs, about 25 minutes more).</item>
/// <item><c>NEMOTRON_GOLDEN_DIR</c>: an extra folder of golden JSON files (see <see cref="GoldenCase"/>), for example a larger set of your own.</item>
/// <item><c>NEMOTRON_GOLDEN_AUDIO</c>: the folder of <c>.f32</c>/<c>.wav</c> clips that goldens refer to and that are not Common Voice (the generated ones: <c>tools/nemotron-golden/README.md</c>).</item>
/// <item><c>NEMOTRON_TEST_THREADS</c>: intra-op threads of the recognizers under test (default 4, the GenAI oracle's).</item>
/// </list>
/// </remarks>
internal static class NemotronTestEnvironment
{
    public const string DefaultVariant = "int4-c056";

    /// <summary>Intra-op threads for the model tests.</summary>
    public static int Threads =>
        int.TryParse(Environment.GetEnvironmentVariable("NEMOTRON_TEST_THREADS"), out var n) && n > 0 ? n : 4;

    // ── repository and main checkout ────────────────────────────────────

    public static string? FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NemotronSpeech.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }

    /// <summary>
    /// The main checkout of a git worktree: <c>.git</c> is a file containing <c>gitdir: &lt;main&gt;/.git/worktrees/&lt;name&gt;</c>,
    /// and that directory's <c>commondir</c> points back to <c>&lt;main&gt;/.git</c>. Null when <paramref name="root"/> is itself the main checkout.
    /// </summary>
    public static string? FindMainCheckout(string root)
    {
        var dotGit = Path.Combine(root, ".git");
        if (!File.Exists(dotGit))
            return null;

        const string prefix = "gitdir:";
        var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
            return null;

        var gitDir = Path.GetFullPath(line[prefix.Length..].Trim(), root);
        string commonDir;
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (File.Exists(commonDirFile))
            commonDir = Path.GetFullPath(File.ReadAllText(commonDirFile).Trim(), gitDir);
        else
            commonDir = Path.GetFullPath(Path.Combine(gitDir, "..", "..")); // <main>/.git/worktrees/<name> -> <main>/.git

        var main = Path.GetDirectoryName(commonDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return main is not null && !string.Equals(Path.GetFullPath(main), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) && Directory.Exists(main)
            ? main
            : null;
    }

    /// <summary>The repository root followed by the main checkout when the root is a worktree.</summary>
    public static IReadOnlyList<string> SearchRoots()
    {
        var roots = new List<string>();
        if (FindRepositoryRoot() is { } root)
        {
            roots.Add(root);
            if (FindMainCheckout(root) is { } main)
                roots.Add(main);
        }
        return roots;
    }

    // ── models ──────────────────────────────────────────────────────────

    public static string FolderName(string variant) => $"nemotron-3.5-asr-streaming-0.6b-onnx-{variant}-cpu";

    /// <summary>The model folder of a variant (<c>int4-c056</c>, <c>int4-c112</c>, <c>fp32-c056</c>, <c>fp32-c112</c>), or null.</summary>
    public static string? LocateModel(string variant)
    {
        var perVariant = Environment.GetEnvironmentVariable("NEMOTRON_MODEL_DIR_" + variant.ToUpperInvariant().Replace('-', '_'));
        if (IsModelFolder(perVariant))
            return perVariant;

        var generic = Environment.GetEnvironmentVariable("NEMOTRON_MODEL_DIR");
        var genericVariant = Environment.GetEnvironmentVariable("NEMOTRON_MODEL_VARIANT") is { Length: > 0 } v ? v : DefaultVariant;
        if (string.Equals(genericVariant, variant, StringComparison.OrdinalIgnoreCase) && IsModelFolder(generic))
            return generic;

        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("NEMOTRON_MODELS_ROOT") is { Length: > 0 } modelsRoot)
        {
            candidates.Add(Path.Combine(modelsRoot, "asr", FolderName(variant)));
            candidates.Add(Path.Combine(modelsRoot, FolderName(variant)));
        }
        foreach (var root in SearchRoots())
        {
            candidates.Add(Path.Combine(root, "models", "asr", FolderName(variant)));
            candidates.Add(Path.Combine(root, "models", FolderName(variant)));
        }
        return candidates.FirstOrDefault(IsModelFolder);
    }

    private static bool IsModelFolder(string? path) => !string.IsNullOrEmpty(path) && NemotronModelConfig.IsNemotronModel(path) && File.Exists(Path.Combine(path, "vocab.txt"));

    // ── audio ───────────────────────────────────────────────────────────

    /// <summary>Folders that may hold the clips goldens refer to, most specific first.</summary>
    public static IReadOnlyList<string> AudioRoots()
    {
        var roots = new List<string>();
        void Add(string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path) && !roots.Contains(path, StringComparer.OrdinalIgnoreCase))
                roots.Add(path);
        }
        Add(Environment.GetEnvironmentVariable("NEMOTRON_GOLDEN_AUDIO"));
        Add(Environment.GetEnvironmentVariable("SPEECHLIB_TEST_AUDIO"));
        foreach (var root in SearchRoots())
            Add(Path.Combine(root, "Test-Audio"));
        foreach (var directory in GoldenCase.Directories())
        {
            Add(directory);
            Add(Path.GetFullPath(Path.Combine(directory, "..", "audio")));   // golden/nemotron next to golden/audio
        }
        return roots;
    }

    /// <summary>Finds a clip by its path relative to an audio root (<c>cv17/en/0001.wav</c>, <c>mixauto.f32</c>), or null.</summary>
    public static string? FindAudio(string relativePath)
    {
        foreach (var root in AudioRoots())
        {
            var path = Path.Combine(root, relativePath);
            if (File.Exists(path))
                return path;
        }
        return null;
    }

    /// <summary>Reads a 16 kHz mono clip: PCM16 <c>.wav</c> (scaled by 1/32768) or raw little-endian float32 <c>.f32</c>.</summary>
    public static float[] LoadAudio(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (path.EndsWith(".f32", StringComparison.OrdinalIgnoreCase))
        {
            var floats = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, floats, 0, floats.Length * 4);
            return floats;
        }
        return DecodePcm16Wav(bytes);
    }

    public static float[] LoadAudioRelative(string relativePath) =>
        LoadAudio(FindAudio(relativePath) ?? throw new FileNotFoundException($"Test audio not found: {relativePath}"));

    /// <summary>
    /// Decodes a 16 kHz mono WAV: PCM16 (scaled by 1/32768) or IEEE float32 (format tag 3, also inside WAVE_FORMAT_EXTENSIBLE).
    /// Anything else (stereo, another sample rate, another sample format) is refused: the model would transcribe it
    /// badly, and a failure that looks like a parity regression is the worst way to learn about a wrong test file.
    /// A data chunk whose size was never finalised (0xFFFFFFFF) or is longer than the file runs to the end of the file.
    /// </summary>
    public static float[] DecodePcm16Wav(byte[] bytes)
    {
        if (bytes.Length < 12 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        var position = 12L;
        var tag = 1;
        int channels = 0, sampleRate = 0, bits = 0;
        while (position + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, (int)position, 4);
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)position + 4));
            if (id == "fmt " && size >= 16)
            {
                var fmt = bytes.AsSpan((int)position + 8);
                tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
                if (tag == 0xFFFE && size >= 26)
                    tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]);
            }
            else if (id == "data")
            {
                if (channels == 0)
                    throw new InvalidDataException("The wav file has a data chunk before its fmt chunk.");
                if (channels != 1 || sampleRate != 16000)
                    throw new InvalidDataException($"The test audio must be 16 kHz mono, this file is {sampleRate} Hz with {channels} channel(s).");
                if (!(tag == 1 && bits == 16) && !(tag == 3 && bits == 32))
                    throw new InvalidDataException($"The test audio must be PCM16 or float32, this file has format tag {tag} with {bits} bits.");

                var available = (int)Math.Min(size, bytes.Length - position - 8);
                if (tag == 3)
                {
                    var floats = new float[available / 4];
                    for (var i = 0; i < floats.Length; i++)
                        floats[i] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan((int)position + 8 + 4 * i)));
                    return floats;
                }

                var samples = new float[available / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan((int)position + 8 + 2 * i)) / 32768f;
                return samples;
            }
            position += 8 + size + (size & 1);
        }
        throw new InvalidDataException("No data chunk in the wav file.");
    }

    // ── small data that ships with the test project ─────────────────────

    public static string DataPath(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "Data", .. parts]);

    public static string[] ReadVocabularyLines() => File.ReadAllLines(DataPath("vocab.txt"));

    public static NemotronModelConfig LoadConfig(string variant) =>
        NemotronModelConfig.Parse(File.ReadAllText(DataPath("config", $"genai_config.{variant}.json")));
}

/// <summary>Runs the test only when the model folder of a variant is available (see <see cref="NemotronTestEnvironment"/>).</summary>
public sealed class NemotronModelFactAttribute : FactAttribute
{
    public NemotronModelFactAttribute(string variant = NemotronTestEnvironment.DefaultVariant)
    {
        if (NemotronTestEnvironment.LocateModel(variant) is null)
            Skip = $"Nemotron model folder '{variant}' not found (set NEMOTRON_MODEL_DIR_{variant.ToUpperInvariant().Replace('-', '_')}, or put it under models/asr of the repository or its main checkout).";
    }
}

/// <summary>Like <see cref="NemotronModelFactAttribute"/> for a theory; also needs the CV17 test audio.</summary>
public sealed class NemotronModelTheoryAttribute : TheoryAttribute
{
    public NemotronModelTheoryAttribute(string variant = NemotronTestEnvironment.DefaultVariant)
    {
        if (NemotronTestEnvironment.LocateModel(variant) is null)
            Skip = $"Nemotron model folder '{variant}' not found.";
        else if (NemotronTestEnvironment.FindAudio("cv17/en/0001.wav") is null)
            Skip = "Test-Audio/cv17 not found (set SPEECHLIB_TEST_AUDIO).";
    }
}

/// <summary>
/// Runs the theory on Windows x64 only: the bit-for-bit statements about the mel front-end rest on the UCRT math library
/// that produced the GenAI dumps (other platforms get a tolerance test instead, see <c>Unit_NemotronStreamingMelTests</c>).
/// </summary>
public sealed class WindowsX64TheoryAttribute : TheoryAttribute
{
    public WindowsX64TheoryAttribute()
    {
        if (!(OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64))
            Skip = "Bit-identity with the GenAI dumps is a Windows x64 statement (UCRT libm); the tolerance variant covers this platform.";
    }
}

/// <summary>
/// Runs the theory over golden files. Every golden is a row; one whose model folder or audio is missing is a skipped row
/// with the reason (<see cref="NemotronGoldenTheoryDiscoverer"/>), so a partial <c>Test-Audio</c> cannot make rows disappear
/// quietly. Without a single runnable case, and without an opt-in to a bigger set (<c>NEMOTRON_GOLDEN_DIR</c>,
/// <c>NEMOTRON_FULL_GOLDEN</c>), the whole theory is one skip; with an opt-in
/// <c>E2E_NemotronGoldenInventoryTests</c> fails when nothing can run.
/// </summary>
[XunitTestCaseDiscoverer("SpeechLib.Nemotron.Tests.NemotronGoldenTheoryDiscoverer", "SpeechLib.Nemotron.Tests")]
public sealed class NemotronGoldenTheoryAttribute : TheoryAttribute
{
    public NemotronGoldenTheoryAttribute(bool vad = false)
    {
        if (!GoldenCase.ExtraSetRequested && GoldenCase.LoadDefault().Problems.Count == 0 && !GoldenCase.Enumerate(vad).Any())
            Skip = vad
                ? "No VAD golden case with a model and audio available."
                : "No golden case with its model folder and audio available.";
    }
}

/// <summary>Theory discoverer that turns a golden row whose model or audio is missing into a skipped test case that says why.</summary>
public sealed class NemotronGoldenTheoryDiscoverer(IMessageSink diagnosticMessageSink) : TheoryDiscoverer(diagnosticMessageSink)
{
    protected override IEnumerable<IXunitTestCase> CreateTestCasesForDataRow(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod,
        IAttributeInfo theoryAttribute, object[] dataRow)
    {
        // rows are (variant, name)
        if (dataRow.Length >= 2 && dataRow[1] is string name && GoldenCase.Find(name)?.WhyNotRunnable() is { } reason)
            return CreateTestCasesForSkippedDataRow(discoveryOptions, testMethod, theoryAttribute, dataRow, reason);
        return base.CreateTestCasesForDataRow(discoveryOptions, testMethod, theoryAttribute, dataRow);
    }
}
