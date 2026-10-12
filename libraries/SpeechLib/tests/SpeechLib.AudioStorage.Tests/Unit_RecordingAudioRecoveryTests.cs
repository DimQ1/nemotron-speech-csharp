using SpeechLib.AudioStorage.Ogg;

namespace SpeechLib.AudioStorage.Tests;

public class Unit_RecordingAudioRecoveryTests
{
    /// <summary>
    /// Records <paramref name="seconds"/> and abandons the writer like a killed process:
    /// copies the .part as it is on disk while the writer is still open.
    /// </summary>
    private static async Task<string> CrashedPartAsync(TempDir dir, RecordingAudioFormat format, double seconds, string name)
    {
        var writer = RecordingAudioWriterFactory.Create(dir.File(name + "-live"), format, queued: false);
        TestAudio.AppendInBatches(writer, TestAudio.Voice(seconds));
        var crashed = dir.File(name + format.FileExtension + ".part");
        using (var src = new FileStream(writer.PartPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var dst = File.Create(crashed))
            src.CopyTo(dst);
        await writer.DisposeAsync();
        return crashed;
    }

    [Fact]
    public async Task Recover_OpusPart_ShouldProducePlayableFileUpToLastFlush()
    {
        using var dir = new TempDir();
        var part = await CrashedPartAsync(dir, RecordingAudioFormat.Standard, 3.5, "a");

        var result = RecordingAudioRecovery.Recover(part);

        Assert.True(result.Recovered);
        Assert.Equal(dir.File("a.ogg"), result.Path);
        Assert.False(File.Exists(part));
        Assert.Equal(RecordingAudioFormat.Standard, result.Format); // read back from the OpusTags
        var pages = Unit_OggOpusRecordingWriterTests.ReadPages(result.Path);
        Assert.True((pages[^1].Flags & OggPageFlags.EndOfStream) != 0);

        var decoded = RecordingAudioReader.ReadMono(result.Path);
        Assert.Equal(result.Samples, decoded.Length);
        Assert.InRange(decoded.Length / 16000.0, 2.8, 3.5); // at most ~1 s lost
    }

    [Fact]
    public async Task Recover_OpusPartWithTornLastPage_ShouldDropOnlyTheTornPage()
    {
        using var dir = new TempDir();
        var part = await CrashedPartAsync(dir, RecordingAudioFormat.Standard, 3.5, "b");
        long fullLength = new FileInfo(part).Length;
        var intactPages = Unit_OggOpusRecordingWriterTests.ReadPages(part);
        var lastPage = intactPages[^1];
        using (var f = new FileStream(part, FileMode.Open))
            f.SetLength(lastPage.Offset + lastPage.Length / 2); // crash in the middle of a page write

        var result = RecordingAudioRecovery.Recover(part);

        var pages = Unit_OggOpusRecordingWriterTests.ReadPages(result.Path);
        Assert.Equal(intactPages.Count - 1, pages.Count);
        Assert.True(new FileInfo(result.Path).Length < fullLength);
        Assert.True((pages[^1].Flags & OggPageFlags.EndOfStream) != 0);
        Assert.Equal(result.Samples, RecordingAudioReader.ReadMono(result.Path).Length);
    }

    [Fact]
    public async Task Recover_OpusPartWithOnlyHeaders_ShouldProduceEmptyValidFile()
    {
        using var dir = new TempDir();
        var part = await CrashedPartAsync(dir, RecordingAudioFormat.Standard, 0.3, "c"); // < 1 s: no audio page yet

        var result = RecordingAudioRecovery.Recover(part);

        Assert.Equal(0, result.Samples);
        Assert.Empty(RecordingAudioReader.ReadMono(result.Path));
    }

    [Fact]
    public async Task Recover_WavPartWithOddByte_ShouldTruncateAndPatchSizes()
    {
        using var dir = new TempDir();
        var part = await CrashedPartAsync(dir, RecordingAudioFormat.High, 2.2, "d");
        using (var f = new FileStream(part, FileMode.Append))
            f.WriteByte(0x7F); // half a sample

        var result = RecordingAudioRecovery.Recover(part);

        long dataBytes = new FileInfo(result.Path).Length - 44;
        Assert.Equal(0, dataBytes % 2);
        Assert.Equal(dataBytes / 2, result.Samples);
        var bytes = File.ReadAllBytes(result.Path);
        Assert.Equal(dataBytes, BitConverter.ToUInt32(bytes, 40));
        Assert.Equal(36 + dataBytes, BitConverter.ToUInt32(bytes, 4));
        Assert.Equal(result.Samples, RecordingAudioReader.ReadMono(result.Path).Length);
    }

    [Fact]
    public void Recover_GarbageFile_ShouldThrowInvalidData()
    {
        using var dir = new TempDir();
        var part = dir.File("e.ogg.part");
        File.WriteAllBytes(part, "OggS"u8.ToArray().Concat(new byte[10]).ToArray());

        Assert.Throws<InvalidDataException>(() => RecordingAudioRecovery.Recover(part));
        Assert.True(File.Exists(part)); // left untouched for inspection
    }

    [Fact]
    public void Recover_WhenFinalExists_ShouldThrowAndKeepBoth()
    {
        using var dir = new TempDir();
        File.WriteAllBytes(dir.File("f.wav"), [1, 2, 3]);
        File.WriteAllBytes(dir.File("f.wav.part"), [1, 2, 3]);

        Assert.Throws<IOException>(() => RecordingAudioRecovery.Recover(dir.File("f.wav.part")));
        Assert.True(File.Exists(dir.File("f.wav")));
    }

    [Fact]
    public async Task FindPartFiles_ShouldListLeftovers()
    {
        using var dir = new TempDir();
        var part = await CrashedPartAsync(dir, RecordingAudioFormat.Standard, 1.2, "g");

        Assert.Contains(part, RecordingAudioRecovery.FindPartFiles(dir.Path));
        Assert.Empty(RecordingAudioRecovery.FindPartFiles(dir.File("missing")));
    }
}
