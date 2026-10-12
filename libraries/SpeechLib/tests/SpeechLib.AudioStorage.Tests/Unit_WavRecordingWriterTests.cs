namespace SpeechLib.AudioStorage.Tests;

public class Unit_WavRecordingWriterTests
{
    [Fact]
    public async Task RoundTrip_ShouldBeLosslessWithinPcm16()
    {
        using var dir = new TempDir();
        var input = TestAudio.Voice(2.05);
        var writer = new WavRecordingWriter(dir.File("a.wav"));
        TestAudio.AppendInBatches(writer, input);
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        var decoded = RecordingAudioReader.ReadMono(result.Path);
        Assert.Equal(input.Length, decoded.Length);
        for (int i = 0; i < input.Length; i++)
            Assert.True(Math.Abs(input[i] - decoded[i]) < 2.0 / 32768, $"sample {i}");
        Assert.Equal(44 + input.Length * 2L, result.Bytes);
    }

    [Fact]
    public async Task PartFile_ShouldHaveValidSizesAfterEachFlush()
    {
        using var dir = new TempDir();
        var writer = new WavRecordingWriter(dir.File("b.wav"), flushIntervalSeconds: 1.0);
        TestAudio.AppendInBatches(writer, TestAudio.Voice(2.5));

        byte[] header = new byte[44];
        using (var f = new FileStream(writer.PartPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            f.ReadExactly(header);
        uint dataBytes = BitConverter.ToUInt32(header, 40);
        Assert.InRange(dataBytes / 2 / 16000.0, 2.0, 2.5);

        await writer.DisposeAsync();
    }

    [Fact]
    public async Task Append_ShouldClipOutOfRangeSamples()
    {
        using var dir = new TempDir();
        var writer = new WavRecordingWriter(dir.File("c.wav"));
        writer.Append([2f, -2f, 0.5f]);
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        var decoded = RecordingAudioReader.ReadMono(result.Path);
        Assert.Equal(32767 / 32768f, decoded[0], 4);
        Assert.Equal(-32767 / 32768f, decoded[1], 4);
        Assert.Equal(0.5f, decoded[2], 3);
    }

    [Fact]
    public void Reader_ShouldRejectWavAtOtherRate()
    {
        using var dir = new TempDir();
        var path = dir.File("d.wav");
        File.WriteAllBytes(path, WavRecordingWriter.BuildHeader(44100, 0));

        Assert.Throws<InvalidDataException>(() => RecordingAudioReader.ReadMono(path));
    }
}
