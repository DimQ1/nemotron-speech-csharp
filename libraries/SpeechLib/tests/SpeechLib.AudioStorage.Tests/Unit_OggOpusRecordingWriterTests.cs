using Concentus;
using Concentus.Oggfile;
using SpeechLib.AudioStorage.Ogg;

namespace SpeechLib.AudioStorage.Tests;

public class Unit_OggOpusRecordingWriterTests
{
    [Fact]
    public async Task RoundTrip_ShouldDecodeSameLengthWithGoodQuality()
    {
        using var dir = new TempDir();
        var input = TestAudio.Voice(3.37);

        var writer = new OggOpusRecordingWriter(dir.File("a.ogg"), RecordingAudioFormat.Standard);
        TestAudio.AppendInBatches(writer, input);
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        var decoded = RecordingAudioReader.ReadMono(result.Path);
        Assert.Equal(input.Length, decoded.Length);
        Assert.Equal(input.Length, result.Samples);
        Assert.True(TestAudio.SnrDb(input, decoded) > 8, $"SNR {TestAudio.SnrDb(input, decoded):F1} dB");
    }

    [Fact]
    public async Task Append_ShouldWriteToPartUntilCompleted()
    {
        using var dir = new TempDir();
        var path = dir.File("b.ogg");
        var writer = new OggOpusRecordingWriter(path, RecordingAudioFormat.Standard);
        writer.Append(TestAudio.Voice(1.5));

        Assert.True(File.Exists(path + ".part"));
        Assert.False(File.Exists(path));

        await writer.CompleteAsync();
        await writer.DisposeAsync();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task Append_ShouldFlushAPageAboutEverySecond()
    {
        using var dir = new TempDir();
        var path = dir.File("c.ogg");
        var writer = new OggOpusRecordingWriter(path, RecordingAudioFormat.Standard, flushIntervalSeconds: 1.0);
        TestAudio.AppendInBatches(writer, TestAudio.Voice(3.5));

        // Read the .part while the writer still holds it (what survives a process kill).
        var onDisk = RecordingAudioReader.ReadMono(path + ".part");
        Assert.InRange(onDisk.Length / 16000.0, 2.8, 3.5);

        await writer.DisposeAsync();
    }

    [Fact]
    public async Task CompletedFile_ShouldBeStandardOggOpus()
    {
        using var dir = new TempDir();
        var input = TestAudio.Voice(2.5);
        var writer = new OggOpusRecordingWriter(dir.File("d.ogg"), RecordingAudioFormat.Standard);
        TestAudio.AppendInBatches(writer, input);
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        // Page structure: BOS head page, tags page, audio pages, EOS on the last one,
        // final granule = pre-skip + samples at 48 kHz (end trimming).
        var pages = ReadPages(result.Path);
        Assert.Equal(OggPageFlags.BeginOfStream, pages[0].Flags);
        Assert.True(OpusHeaders.TryParseHead(pages[0].Body, out int channels, out int preSkip, out int rate));
        Assert.Equal(1, channels);
        Assert.Equal(16000, rate);
        Assert.True(preSkip > 0);
        Assert.True(OpusHeaders.IsTags(pages[1].Body));
        Assert.True((pages[^1].Flags & OggPageFlags.EndOfStream) != 0);
        Assert.Equal(preSkip + input.Length * 3L, pages[^1].Granule);
        Assert.All(pages, p => Assert.Equal(pages[0].Serial, p.Serial));
        Assert.Equal(Enumerable.Range(0, pages.Count).Select(i => (uint)i), pages.Select(p => p.Sequence));

        // An independent demuxer (Concentus.Oggfile) reads it and sees our tags.
        using var stream = File.OpenRead(result.Path);
        var ogg = new OpusOggReadStream(OpusCodecFactory.CreateDecoder(48000, 1), stream);
        long decoded48k = 0;
        while (ogg.HasNextPacket)
            decoded48k += ogg.DecodeNextPacket()?.Length ?? 0;
        Assert.Null(ogg.LastError);
        Assert.True(decoded48k >= input.Length * 3L, $"decoded {decoded48k} samples @48k");
        Assert.Contains("ENCODER", ogg.Tags.Fields.Keys);
    }

    [Fact]
    public async Task Complete_WithNoAudio_ShouldProduceValidEmptyFile()
    {
        using var dir = new TempDir();
        var writer = new OggOpusRecordingWriter(dir.File("e.ogg"), RecordingAudioFormat.Standard);
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        Assert.Equal(0, result.Samples);
        Assert.Empty(RecordingAudioReader.ReadMono(result.Path));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public async Task Bitrate_ShouldBeNearTarget(int kbps)
    {
        using var dir = new TempDir();
        var writer = new OggOpusRecordingWriter(dir.File($"f{kbps}.ogg"), new RecordingAudioFormat(RecordingAudioCodec.OggOpus, kbps));
        TestAudio.AppendInBatches(writer, TestAudio.Voice(10));
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        double actualKbps = result.Bytes * 8 / 10.0 / 1000;
        Assert.InRange(actualKbps, kbps * 0.5, kbps * 1.3);
    }

    [Fact]
    public void Constructor_ShouldRejectUnsupportedSampleRate()
    {
        using var dir = new TempDir();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OggOpusRecordingWriter(dir.File("g.ogg"), RecordingAudioFormat.Standard, sampleRate: 44100));
    }

    internal static List<OggPage> ReadPages(string path)
    {
        using var stream = File.OpenRead(path);
        var reader = new OggPageReader(stream);
        var pages = new List<OggPage>();
        while (reader.TryReadPage(out var page))
            pages.Add(page);
        Assert.False(reader.StoppedOnDamage);
        return pages;
    }
}
