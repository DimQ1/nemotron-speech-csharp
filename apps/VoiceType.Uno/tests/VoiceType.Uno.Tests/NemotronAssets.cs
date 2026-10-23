using SpeechLib.AudioStorage;
using Xunit;

namespace VoiceType.Uno.Tests;

/// <summary>
/// Where the gitignored Nemotron model and the Common Voice clips live, for the tests that run the real recognizer. Each lookup
/// tries an environment variable, the repository root (walking up from the test binaries to <c>NemotronSpeech.slnx</c>) and,
/// when that root is a git worktree without the files, the main checkout it was created from.
/// <c>NEMOTRON_MODEL_DIR_INT4_C056</c> names the model folder, <c>SPEECHLIB_TEST_AUDIO</c> the Test-Audio folder.
/// </summary>
internal static class NemotronAssets
{
    private const string ModelFolderName = "nemotron-3.5-asr-streaming-0.6b-onnx-int4-c056-cpu";

    public static string? ModelFolder { get; } = FindModelFolder();

    public static string? FindClip(string relativePath) =>
        Candidates("SPEECHLIB_TEST_AUDIO", "Test-Audio", relativePath).FirstOrDefault(File.Exists);

    /// <summary>A 16 kHz mono clip of Test-Audio, e.g. <c>cv17/en/0001.wav</c>.</summary>
    public static float[] LoadClip(string relativePath) =>
        RecordingAudioReader.ReadMono(FindClip(relativePath) ?? throw new FileNotFoundException(relativePath), 16000);

    /// <summary>
    /// Deterministic room tone (an xorshift32 stream, about -69 dBFS: a quiet room, below the noise gate of the gain control; a
    /// louder tone would be taken for speech and raised by up to 26 dB when a stream starts in it).
    /// </summary>
    public static float[] RoomTone(double seconds, uint seed)
    {
        var tone = new float[(int)(seconds * 16000)];
        var state = seed * 2654435761u + 0x9E3779B9u;
        for (var i = 0; i < tone.Length; i++)
        {
            var sum = 0f;
            for (var k = 0; k < 12; k++)
            {
                state ^= state << 13;
                state ^= state >> 17;
                state ^= state << 5;
                sum += (state >> 8) / 16777216f;
            }
            tone[i] = (sum - 6f) * (12f / 32768f);
        }
        return tone;
    }

    /// <summary>Clips and pauses one after the other.</summary>
    public static float[] Concat(params float[][] parts)
    {
        var all = new float[parts.Sum(p => p.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(all, offset);
            offset += part.Length;
        }
        return all;
    }

    private static string? FindModelFolder()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("NEMOTRON_MODEL_DIR_INT4_C056");
        if (IsModel(fromEnvironment))
            return fromEnvironment;
        return Candidates("NEMOTRON_MODELS_ROOT", "models", Path.Combine("asr", ModelFolderName)).FirstOrDefault(IsModel);
    }

    private static bool IsModel(string? folder) =>
        !string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(folder, "genai_config.json")) && File.Exists(Path.Combine(folder, "vocab.txt"));

    private static IEnumerable<string> Candidates(string environmentVariable, string repositoryFolder, string relativePath)
    {
        if (Environment.GetEnvironmentVariable(environmentVariable) is { Length: > 0 } root)
        {
            yield return Path.Combine(root, relativePath);
            if (repositoryFolder == "models")
                yield return Path.Combine(root, Path.GetFileName(relativePath));
        }
        foreach (var repository in RepositoryRoots())
            yield return Path.Combine(repository, repositoryFolder, relativePath);
    }

    /// <summary>The repository root followed by the main checkout when the root is a git worktree (its <c>.git</c> file names the common dir).</summary>
    private static IEnumerable<string> RepositoryRoots()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NemotronSpeech.slnx")))
            directory = directory.Parent;
        if (directory is null)
            yield break;

        yield return directory.FullName;
        var dotGit = Path.Combine(directory.FullName, ".git");
        if (!File.Exists(dotGit))
            yield break;

        const string prefix = "gitdir:";
        var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal));
        if (line is null)
            yield break;
        var gitDir = Path.GetFullPath(line[prefix.Length..].Trim(), directory.FullName);
        var commonFile = Path.Combine(gitDir, "commondir");
        var commonDir = File.Exists(commonFile)
            ? Path.GetFullPath(File.ReadAllText(commonFile).Trim(), gitDir)
            : Path.GetFullPath(Path.Combine(gitDir, "..", ".."));
        if (Path.GetDirectoryName(commonDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { } main
            && !string.Equals(Path.GetFullPath(main), directory.FullName, StringComparison.OrdinalIgnoreCase))
            yield return main;
    }
}

/// <summary>Runs the test only when the Nemotron INT4 model and the Common Voice test clips are available (see <see cref="NemotronAssets"/>).</summary>
public sealed class NemotronE2EFactAttribute : FactAttribute
{
    public NemotronE2EFactAttribute()
    {
        if (NemotronAssets.ModelFolder is null)
            Skip = "Nemotron INT4 c056 model folder not found (set NEMOTRON_MODEL_DIR_INT4_C056 or put it under models/asr of the repository or its main checkout).";
        else if (NemotronAssets.FindClip("cv17/en/0001.wav") is null)
            Skip = "Test-Audio/cv17 not found (set SPEECHLIB_TEST_AUDIO).";
    }
}
