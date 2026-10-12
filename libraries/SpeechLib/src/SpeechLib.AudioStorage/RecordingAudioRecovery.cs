using System.Buffers.Binary;
using SpeechLib.AudioStorage.Ogg;

namespace SpeechLib.AudioStorage;

/// <summary>
/// Turns a leftover <c>&lt;file&gt;.part</c> (process killed mid-recording) into a valid,
/// playable <c>&lt;file&gt;</c>. Call it at startup for every <c>.part</c> that has no live
/// session. Gaps are not stored in the audio file, so the result has none — keep them
/// in the session manifest.
/// </summary>
public static class RecordingAudioRecovery
{
    public const string PartSuffix = ".part";

    /// <summary>Lists unfinished recordings under <paramref name="directory"/> (recursive).</summary>
    public static IEnumerable<string> FindPartFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*" + PartSuffix, SearchOption.AllDirectories)
            : [];

    /// <summary>The final path a <c>.part</c> file recovers to.</summary>
    public static string FinalPathOf(string partPath) =>
        partPath.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)
            ? partPath[..^PartSuffix.Length]
            : throw new ArgumentException($"Not a '{PartSuffix}' file: {partPath}", nameof(partPath));

    /// <summary>
    /// Repairs <paramref name="partPath"/> in place (truncates a torn last page / odd byte,
    /// closes the Ogg stream or patches WAV sizes) and renames it to its final path.
    /// </summary>
    /// <exception cref="InvalidDataException">The file holds no usable header (crash before the first flush).</exception>
    /// <exception cref="IOException">The final path already exists.</exception>
    public static RecordingAudioResult Recover(string partPath)
    {
        var finalPath = FinalPathOf(Path.GetFullPath(partPath));
        if (File.Exists(finalPath))
            throw new IOException($"Cannot recover '{partPath}': '{finalPath}' already exists.");

        RecordingAudioResult result;
        using (var file = new FileStream(partPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Span<byte> magic = stackalloc byte[4];
            if (file.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4)
                throw new InvalidDataException($"'{partPath}' is empty.");
            file.Position = 0;

            result = magic.SequenceEqual("OggS"u8) ? RepairOgg(file, finalPath)
                : magic.SequenceEqual("RIFF"u8) ? RepairWav(file, finalPath)
                : throw new InvalidDataException($"'{partPath}' is neither Ogg nor WAV.");
            file.Flush(flushToDisk: true);
        }

        File.Move(partPath, finalPath);
        return result with { Bytes = new FileInfo(finalPath).Length };
    }

    private static RecordingAudioResult RepairOgg(FileStream file, string finalPath)
    {
        var reader = new OggPageReader(file);
        OggPage? last = null;
        int inputRate = 0, preSkip48k = -1;
        bool sawTags = false;
        var format = new RecordingAudioFormat(RecordingAudioCodec.OggOpus);
        while (reader.TryReadPage(out var page))
        {
            if (preSkip48k < 0)
            {
                var first = page.Packets().FirstOrDefault();
                if (first.Packet.Array is null || !OpusHeaders.TryParseHead(first.Packet, out _, out preSkip48k, out inputRate))
                    throw new InvalidDataException("First Ogg page is not an OpusHead.");
            }
            else if (!sawTags)
            {
                var tags = page.Packets().FirstOrDefault(p => p.Complete && OpusHeaders.IsTags(p.Packet));
                if (tags.Packet.Array is not null)
                {
                    sawTags = true;
                    format = OpusHeaders.FormatFromTags(OpusHeaders.ParseTagComments(tags.Packet));
                }
            }
            last = page;
            if ((page.Flags & OggPageFlags.EndOfStream) != 0)
                break;
        }

        if (last is null || preSkip48k < 0 || !sawTags)
            throw new InvalidDataException("The recording ended before its Opus headers were written.");

        var lastPage = last.Value;
        file.SetLength(lastPage.Offset + lastPage.Length);
        if ((lastPage.Flags & OggPageFlags.EndOfStream) == 0)
        {
            var fixedPage = OggPage.Build(lastPage.Flags | OggPageFlags.EndOfStream, lastPage.Granule,
                lastPage.Serial, lastPage.Sequence, lastPage.Lacing, lastPage.Body);
            file.Position = lastPage.Offset;
            file.Write(fixedPage);
        }

        int rate = inputRate > 0 ? inputRate : 48000;
        long samples48k = Math.Max(0, lastPage.Granule - preSkip48k);
        long samples = samples48k * rate / OpusHeaders.GranuleRate;
        return new RecordingAudioResult(finalPath, format, rate, samples, 0, [], Recovered: true);
    }

    private static RecordingAudioResult RepairWav(FileStream file, string finalPath)
    {
        Span<byte> header = stackalloc byte[WavRecordingWriter.HeaderSize];
        if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length
            || !header[8..12].SequenceEqual("WAVE"u8) || !header[36..40].SequenceEqual("data"u8))
            throw new InvalidDataException("Not a SpeechLib recording WAV header.");

        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        long dataBytes = (file.Length - WavRecordingWriter.HeaderSize) & ~1L;
        file.SetLength(WavRecordingWriter.HeaderSize + dataBytes);
        WavRecordingWriter.PatchSizes(file, dataBytes);
        return new RecordingAudioResult(finalPath, RecordingAudioFormat.High, sampleRate, dataBytes / 2, 0, [], Recovered: true);
    }
}
