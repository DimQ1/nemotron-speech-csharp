using System.Buffers.Binary;
using Concentus;
using Concentus.Structs;
using SpeechLib.AudioStorage.Ogg;

namespace SpeechLib.AudioStorage;

/// <summary>
/// Decodes a stored recording to mono float samples for offline re-recognition.
/// Reads finished files and unfinished <c>.part</c> files alike (stops at the first
/// damaged page).
/// </summary>
public static class RecordingAudioReader
{
    /// <summary>The rate offline recognizers expect.</summary>
    public const int RecognizerSampleRate = 16000;

    /// <summary>
    /// Decodes <paramref name="path"/> (Ogg Opus or WAV PCM16/float32, any channel count)
    /// to mono float at <paramref name="sampleRate"/>.
    /// Opus decodes natively to 8/12/16/24/48 kHz; a WAV must already be at <paramref name="sampleRate"/>.
    /// </summary>
    public static float[] ReadMono(string path, int sampleRate = RecognizerSampleRate)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return ReadMono(stream, sampleRate);
    }

    /// <inheritdoc cref="ReadMono(string,int)"/>
    public static float[] ReadMono(Stream stream, int sampleRate = RecognizerSampleRate)
    {
        Span<byte> magic = stackalloc byte[4];
        long start = stream.Position;
        if (stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false) < 4)
            throw new InvalidDataException("Audio file is empty.");
        stream.Position = start;
        if (magic.SequenceEqual("OggS"u8)) return DecodeOggOpus(stream, sampleRate);
        if (magic.SequenceEqual("RIFF"u8)) return DecodeWav(stream, sampleRate);
        throw new InvalidDataException("Unsupported audio file (expected Ogg Opus or WAV).");
    }

    private static float[] DecodeOggOpus(Stream stream, int sampleRate)
    {
        if (!OggOpusRecordingWriter.SupportedSampleRates.Contains(sampleRate))
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Opus decodes to 8, 12, 16, 24 or 48 kHz.");

        var reader = new OggPageReader(stream);
        IOpusDecoder? decoder = null;
        int channels = 0;
        long preSkip = 0;
        long lastGranule = -1;
        int headerPackets = 0;
        var pending = new List<byte>(); // packet spanning pages
        var output = new List<float>(sampleRate * 60);
        float[] pcm = new float[sampleRate * 120 / 1000 * 2]; // max 120 ms, up to 2 channels

        while (reader.TryReadPage(out var page))
        {
            foreach (var (segment, complete) in page.Packets())
            {
                pending.AddRange(segment);
                if (!complete) break;
                var packet = pending.ToArray();
                pending.Clear();

                if (headerPackets == 0)
                {
                    if (!OpusHeaders.TryParseHead(packet, out channels, out int preSkip48k, out _) || channels > 2)
                        throw new InvalidDataException("Not a mono/stereo Ogg Opus stream.");
                    preSkip = (long)preSkip48k * sampleRate / OpusHeaders.GranuleRate;
#pragma warning disable CS0618 // managed decoder on purpose (no native probing)
                    decoder = new OpusDecoder(sampleRate, channels);
#pragma warning restore CS0618
                    headerPackets++;
                    continue;
                }
                if (headerPackets == 1)
                {
                    headerPackets++; // OpusTags
                    continue;
                }

                int frames = decoder!.Decode(packet, pcm, pcm.Length / channels, false);
                if (frames < 0) continue; // corrupt packet: skip, keep timing of the rest
                for (int i = 0; i < frames; i++)
                {
                    output.Add(channels == 1 ? pcm[i] : (pcm[2 * i] + pcm[2 * i + 1]) * 0.5f);
                }
            }
            if (page.Granule >= 0 && headerPackets >= 2)
                lastGranule = page.Granule;
            if ((page.Flags & OggPageFlags.EndOfStream) != 0)
                break;
        }

        if (decoder is null)
            throw new InvalidDataException("Ogg stream has no OpusHead.");

        long end = output.Count;
        if (lastGranule >= 0)
            end = Math.Min(end, lastGranule * sampleRate / OpusHeaders.GranuleRate);
        long skip = Math.Min(preSkip, end);
        int count = (int)Math.Max(0, end - skip);
        var result = new float[count];
        output.CopyTo((int)skip, result, 0, count);
        return result;
    }

    private static float[] DecodeWav(Stream stream, int sampleRate)
    {
        using var br = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        br.ReadBytes(12); // RIFF, size, WAVE
        int channels = 0, rate = 0, bits = 0, formatTag = 0;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(br.ReadBytes(4));
            uint size = br.ReadUInt32();
            if (id == "fmt ")
            {
                var fmt = br.ReadBytes((int)size);
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                rate = BinaryPrimitives.ReadInt32LittleEndian(fmt.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (formatTag == 0xFFFE && fmt.Length >= 26) // WAVE_FORMAT_EXTENSIBLE: subformat GUID starts with the tag
                    formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                if ((size & 1) != 0) stream.Seek(1, SeekOrigin.Current);
            }
            else if (id == "data")
            {
                if (channels == 0) throw new InvalidDataException("WAV data before fmt chunk.");
                if (rate != sampleRate)
                    throw new InvalidDataException($"WAV is {rate} Hz; expected {sampleRate} Hz (resample with SpeechLib.Audio.StreamingResampler).");
                // A .part or >4 GB file may carry a stale/clamped size: read to end of file instead.
                long available = stream.Length - stream.Position;
                long dataBytes = size == 0 || size == uint.MaxValue || size > available ? available : size;
                return ReadPcm(br, dataBytes, channels, bits, formatTag);
            }
            else
            {
                stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }
        throw new InvalidDataException("WAV has no data chunk.");
    }

    private static float[] ReadPcm(BinaryReader br, long dataBytes, int channels, int bits, int formatTag)
    {
        int bytesPerSample = bits / 8;
        bool isFloat = formatTag == 3 && bits == 32;
        if (!isFloat && !(formatTag == 1 && bits == 16))
            throw new InvalidDataException($"Unsupported WAV encoding (tag {formatTag}, {bits}-bit).");

        long frames = dataBytes / (bytesPerSample * channels);
        var output = new float[frames];
        for (long f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += isFloat ? br.ReadSingle() : br.ReadInt16() / 32768f;
            output[f] = sum / channels;
        }
        return output;
    }
}
