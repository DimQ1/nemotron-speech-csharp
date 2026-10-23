using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

/// <summary>
/// The golden files themselves and the loaders that read them and the audio they refer to: a golden that cannot be read must
/// fail loudly, never disappear from the suite, and the tracked oracle must stay intact. No model needed.
/// </summary>
public sealed class Unit_GoldenFilesTests : IDisposable
{
    private readonly List<string> _temp = new();

    public void Dispose()
    {
        foreach (var directory in _temp)
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void RepositoryGoldens_ShouldAllBeReadable()
    {
        var directory = NemotronTestEnvironment.DataPath("golden");

        var load = GoldenCase.LoadFrom([directory]);

        Assert.Empty(load.Problems);
        var files = Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Count();
        Assert.True(files > 0);
        Assert.Equal(files, load.Cases.Count);                     // every file is a case, none vanished
        Assert.All(load.Cases, c => Assert.NotEmpty(c.Chunks));
    }

    [Fact]
    public void FullGoldenSet_ShouldStayIntactInTheRepository()
    {
        // The oracle is the only record of what ORT GenAI produced; a missing, truncated or hand-edited file must be noticed.
        var directory = NemotronTestEnvironment.DataPath("golden-full");
        Assert.True(Directory.Exists(directory), $"{directory} is missing: the full golden set is tracked in the repository");

        var load = GoldenCase.LoadFrom([directory]);

        Assert.Empty(load.Problems);
        Assert.Equal(210, load.Cases.Count(c => !c.Vad));
        Assert.Equal(48, load.Cases.Count(c => c.Vad));
        Assert.Equal([("fp32-c056", 13), ("fp32-c112", 13), ("int4-c056", 131), ("int4-c112", 53)],
            load.Cases.Where(c => !c.Vad).GroupBy(c => c.Variant).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => (g.Key, g.Count())));
        Assert.Equal(load.Cases.Count, load.Cases.Select(c => c.Name).Distinct().Count());
        Assert.Contains(load.Cases, c => c.LangId == "101");        // the auto-language run
        Assert.All(load.Cases, c => Assert.True(c.ChunkSamples is 8960 or 17920));
        Assert.All(load.Cases.Where(c => !c.Vad), c => Assert.Equal(c.TokenCount, c.Chunks.Sum(chunk => chunk.Ids.Length)));
    }

    [Fact]
    public void LoadFrom_ShouldReportEveryFileThatIsNotAReadableGoldenAndIgnoreOnlyTheManifests()
    {
        var directory = NewDirectory();
        Directory.CreateDirectory(Path.Combine(directory, "checks"));
        Directory.CreateDirectory(Path.Combine(directory, "int4-c056"));
        const string valid = """{"variant":"int4-c056","clip":"en0001","chunkSamples":8960,"chunks":[{"feed":0,"processed":true,"chunk":0,"ids":[1,2]},{"feed":"flush","processed":true,"ids":[]}]}""";
        File.WriteAllText(Path.Combine(directory, "int4-c056", "en0001.json"), valid);
        File.WriteAllText(Path.Combine(directory, "clips.json"), "{\"clips\":[]}");                                  // manifest: ignored
        File.WriteAllText(Path.Combine(directory, "index.json"), "[]");                                              // manifest: ignored
        File.WriteAllText(Path.Combine(directory, "checks", "quirks.int4-c056.json"), "not json at all");           // evidence folder: ignored

        File.WriteAllText(Path.Combine(directory, "int4-c056", "truncated.json"), valid[..60]);                      // cut off in the middle
        File.WriteAllText(Path.Combine(directory, "int4-c056", "noids.json"), """{"variant":"int4-c056","clip":"x","chunks":[{"feed":0,"processed":true}]}""");
        File.WriteAllText(Path.Combine(directory, "int4-c056", "novariant.json"), """{"clip":"y","chunks":[]}""");
        File.WriteAllText(Path.Combine(directory, "int4-c056", "notagolden.json"), """{"hello":"world"}""");
        File.WriteAllText(Path.Combine(directory, "int4-c056", "array.json"), "[1,2,3]");

        var load = GoldenCase.LoadFrom([directory]);

        Assert.Equal(["int4-c056_en0001"], load.Cases.Select(c => c.Name));
        Assert.Equal(5, load.Problems.Count);
        foreach (var name in new[] { "truncated.json", "noids.json", "novariant.json", "notagolden.json", "array.json" })
            Assert.Contains(load.Problems, p => p.Contains(name));
        Assert.DoesNotContain(load.Problems, p => p.Contains("clips.json") || p.Contains("index.json") || p.Contains("quirks"));
    }

    [Fact]
    public void Rows_ShouldListEveryGoldenOfTheDefaultSetOnce()
    {
        // GoldenCase.Rows reads the process-wide default set and throws (failing the discovery of every theory) when a file of it
        // cannot be read: the Problems list checked above.
        var rows = GoldenCase.Rows(vad: false).ToList();
        var vadRows = GoldenCase.Rows(vad: true).ToList();

        Assert.NotEmpty(rows);                                     // the repository set: at least 28 plain goldens
        Assert.NotEmpty(vadRows);
        Assert.Equal(rows.Count, rows.Select(r => (string)r[1]).Distinct().Count());
        Assert.All(rows.Concat(vadRows), r => Assert.Equal(2, r.Length));
    }

    [Fact]
    public void WhyNotRunnable_ShouldNameTheMissingModelOrAudio()
    {
        var withoutModel = GoldenCase.LoadFrom([NemotronTestEnvironment.DataPath("golden")]).Cases.First();

        // whichever of model / audio is missing on this machine is named; a runnable case says nothing
        var reason = withoutModel.WhyNotRunnable();
        if (reason is not null)
            Assert.True(reason.Contains("model folder") || reason.Contains("audio"), reason);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData("no", false)]
    [InlineData(null, false)]
    public void IsTruthy_ShouldAcceptTheUsualSwitchValues(string? value, bool expected)
    {
        Assert.Equal(expected, GoldenCase.IsTruthy(value));
    }

    // ── the wav reader the goldens' audio goes through ──────────────────

    private static byte[] Wav(int channels, int sampleRate, int bits, int tag, byte[] data, uint? declaredSize = null)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + data.Length); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)tag); w.Write((short)channels); w.Write(sampleRate);
        w.Write(sampleRate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write((short)bits);
        w.Write("data"u8.ToArray()); w.Write(declaredSize ?? (uint)data.Length); w.Write(data);
        return stream.ToArray();
    }

    [Fact]
    public void DecodePcm16Wav_Mono16kPcm16AndFloat32_ShouldDecode()
    {
        var pcm = Wav(1, 16000, 16, 1, [0x00, 0x40, 0x00, 0xC0]);        // 0.5, -0.5
        var floats = Wav(1, 16000, 32, 3, BitConverter.GetBytes(0.25f).Concat(BitConverter.GetBytes(-1f)).ToArray());

        Assert.Equal(new[] { 0.5f, -0.5f }, NemotronTestEnvironment.DecodePcm16Wav(pcm));
        Assert.Equal(new[] { 0.25f, -1f }, NemotronTestEnvironment.DecodePcm16Wav(floats));
    }

    [Fact]
    public void DecodePcm16Wav_StereoOrOtherRateOrFormat_ShouldBeRefused()
    {
        var samples = new byte[8];

        var stereo = Assert.Throws<InvalidDataException>(() => NemotronTestEnvironment.DecodePcm16Wav(Wav(2, 16000, 16, 1, samples)));
        var rate = Assert.Throws<InvalidDataException>(() => NemotronTestEnvironment.DecodePcm16Wav(Wav(1, 44100, 16, 1, samples)));
        var format = Assert.Throws<InvalidDataException>(() => NemotronTestEnvironment.DecodePcm16Wav(Wav(1, 16000, 8, 1, samples)));

        Assert.Contains("2 channel", stereo.Message);
        Assert.Contains("44100", rate.Message);
        Assert.Contains("8 bits", format.Message);
        Assert.Throws<InvalidDataException>(() => NemotronTestEnvironment.DecodePcm16Wav([1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => NemotronTestEnvironment.DecodePcm16Wav(System.Text.Encoding.ASCII.GetBytes("RIFFxxxxAVI LIST....")));
    }

    [Fact]
    public void DecodePcm16Wav_UnfinalisedOrOversizedDataChunk_ShouldRunToTheEndOfTheFile()
    {
        var data = new byte[] { 0x00, 0x40, 0x00, 0xC0, 0x00, 0x20 };

        Assert.Equal(3, NemotronTestEnvironment.DecodePcm16Wav(Wav(1, 16000, 16, 1, data, declaredSize: 0xFFFFFFFF)).Length);   // streamed wav, size never written
        Assert.Equal(3, NemotronTestEnvironment.DecodePcm16Wav(Wav(1, 16000, 16, 1, data, declaredSize: 1000)).Length);
    }

    private string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "nemotron-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        _temp.Add(directory);
        return directory;
    }
}
