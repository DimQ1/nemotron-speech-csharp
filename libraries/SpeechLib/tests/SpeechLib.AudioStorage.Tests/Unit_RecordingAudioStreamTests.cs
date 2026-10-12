namespace SpeechLib.AudioStorage.Tests;

public class Unit_RecordingAudioStreamTests
{
    private static async Task<string> WriteAsync(TempDir dir, RecordingAudioFormat format, float[] samples, string name = "a")
    {
        var writer = RecordingAudioWriterFactory.Create(dir.File(name), format, queued: false);
        TestAudio.AppendInBatches(writer, samples);
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();
        return result.Path;
    }

    private static float[] ReadAll(RecordingAudioStream stream, int chunk = 777)
    {
        var all = new List<float>();
        var buffer = new float[chunk];
        int n;
        while ((n = stream.Read(buffer)) > 0)
            all.AddRange(buffer.AsSpan(0, n).ToArray());
        return all.ToArray();
    }

    [Fact]
    public async Task OggOpus_ShouldStreamSameSamplesAsReadMono()
    {
        using var dir = new TempDir();
        var path = await WriteAsync(dir, RecordingAudioFormat.Standard, TestAudio.Voice(4.3));

        var expected = RecordingAudioReader.ReadMono(path);
        using var stream = RecordingAudioStream.Open(path);

        Assert.Equal(16000, stream.SampleRate);
        Assert.Equal(expected.Length, stream.Length);
        var actual = ReadAll(stream);
        Assert.Equal(expected, actual);
        Assert.Equal(stream.Length, stream.Position);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.013)]
    [InlineData(1.0)]
    [InlineData(1.517)]
    [InlineData(3.9)]
    public async Task OggOpus_SeekShouldLandOnTheSameAudio(double seconds)
    {
        using var dir = new TempDir();
        var path = await WriteAsync(dir, RecordingAudioFormat.Standard, TestAudio.Voice(4.3));
        var expected = RecordingAudioReader.ReadMono(path);
        using var stream = RecordingAudioStream.Open(path);

        long target = (long)(seconds * 16000);
        stream.Seek(4 * 16000);   // somewhere else first
        stream.Read(new float[100]);
        stream.Seek(target);
        Assert.Equal(target, stream.Position);
        var actual = ReadAll(stream);

        Assert.Equal(expected.Length - target, actual.Length);
        // Pre-roll lets the decoder converge: the audio matches the sequential decode closely
        // (exactly at 0, where nothing is skipped).
        double snr = TestAudio.SnrDb(expected.AsSpan((int)target), actual);
        Assert.True(snr > 25, $"SNR after seek {snr:F1} dB");
    }

    [Fact]
    public async Task Wav_ShouldStreamAndSeekExactly()
    {
        using var dir = new TempDir();
        var input = TestAudio.Voice(2.2);
        var path = await WriteAsync(dir, RecordingAudioFormat.High, input);
        var expected = RecordingAudioReader.ReadMono(path);
        using var stream = RecordingAudioStream.Open(path);

        Assert.Equal(expected.Length, stream.Length);
        Assert.Equal(expected, ReadAll(stream));

        stream.Seek(12345);
        var tail = ReadAll(stream, 1000);
        Assert.Equal(expected.AsSpan(12345).ToArray(), tail);
    }

    [Fact]
    public async Task Seek_ShouldClampPastTheEnd()
    {
        using var dir = new TempDir();
        var path = await WriteAsync(dir, RecordingAudioFormat.Standard, TestAudio.Voice(1.2));
        using var stream = RecordingAudioStream.Open(path);

        stream.Seek(long.MaxValue);
        Assert.Equal(stream.Length, stream.Position);
        Assert.Equal(0, stream.Read(new float[64]));

        stream.Seek(-5);
        Assert.Equal(0, stream.Position);
        Assert.True(stream.Read(new float[64]) > 0);
    }

    [Fact]
    public async Task PartFile_ShouldPlayWhatWasFlushed()
    {
        using var dir = new TempDir();
        var path = dir.File("c.ogg");
        var writer = new OggOpusRecordingWriter(path, RecordingAudioFormat.Standard, flushIntervalSeconds: 1.0);
        TestAudio.AppendInBatches(writer, TestAudio.Voice(3.5));

        var expected = RecordingAudioReader.ReadMono(path + ".part");
        using (var stream = RecordingAudioStream.Open(path + ".part"))
        {
            Assert.Equal(expected.Length, stream.Length);
            Assert.Equal(expected, ReadAll(stream));
        }
        await writer.DisposeAsync();
    }
}
