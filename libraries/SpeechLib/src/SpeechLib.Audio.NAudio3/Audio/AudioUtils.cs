using NAudio.Wave;

namespace SpeechLib.Audio;

/// <summary>Audio file loading and one-shot format conversion for the NAudio 3 provider.</summary>
public static class AudioUtils
{
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>Convert raw interleaved PCM / IEEE-float bytes to float32 mono samples.</summary>
    public static float[] Convert(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(format);

        var sampleFormat = ToSampleFormat(format);
        var frames = bytesRecorded / (PcmSampleDecoder.BytesPerSample(sampleFormat) * format.Channels);
        var mono = new float[frames];
        PcmSampleDecoder.DecodeToMono(buffer.AsSpan(0, bytesRecorded), sampleFormat, format.Channels, mono);
        return mono;
    }

    /// <summary>
    /// One-shot anti-aliased resample (windowed-sinc, see <see cref="StreamingResampler"/>)
    /// with optional gain. The output covers the full input duration.
    /// </summary>
    public static float[] Resample(float[] samples, int fromRate, int toRate, float gain = 1f)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (fromRate == toRate)
        {
            if (gain == 1f)
                return (float[])samples.Clone();

            var scaled = new float[samples.Length];
            for (var i = 0; i < samples.Length; i++)
                scaled[i] = samples[i] * gain;
            return scaled;
        }

        var resampler = new StreamingResampler(fromRate, toRate);
        var output = new float[resampler.MaxOutputCount(samples.Length) + resampler.FlushOutputCount];
        var count = resampler.Process(samples, output);
        count += resampler.Flush(output.AsSpan(count));

        var result = new float[count];
        Array.Copy(output, result, count);
        if (gain != 1f)
        {
            for (var i = 0; i < count; i++)
                result[i] *= gain;
        }
        return result;
    }

    /// <summary>
    /// Load an audio file as float32 mono at the target sample rate.
    /// WAV (PCM 8/16/24/32-bit or IEEE float, any channel count) is parsed portably so it
    /// also works on Linux. Other containers (MP3, M4A, FLAC, …) are decoded through
    /// Media Foundation and therefore only load on Windows.
    /// </summary>
    public static float[] LoadFile(string path, int targetRate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (targetRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRate));

        return IsRiffWave(path)
            ? LoadWav(path, targetRate)
            : LoadWithMediaFoundation(path, targetRate);
    }

    private static bool IsRiffWave(string path)
    {
        Span<byte> header = stackalloc byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header);
        return read == 12
               && header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
               && header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E';
    }

    private static float[] LoadWav(string path, int targetRate)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44)
            throw new InvalidOperationException($"Not a valid WAV file (too small): {path}");

        int channels = 1;
        int sampleRate = targetRate;
        int bitsPerSample = 16;
        int audioFormat = 1; // 1 = PCM, 3 = IEEE float, 0xFFFE = extensible
        Guid subFormat = Guid.Empty;
        int dataOffset = -1;
        int dataLength = 0;

        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            var chunkSize = BitConverter.ToInt32(bytes, position + 4);

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BitConverter.ToUInt16(bytes, position + 8);
                channels = BitConverter.ToInt16(bytes, position + 10);
                sampleRate = BitConverter.ToInt32(bytes, position + 12);
                bitsPerSample = BitConverter.ToInt16(bytes, position + 22);
                if (audioFormat == 0xFFFE && chunkSize >= 40)
                    subFormat = new Guid(bytes.AsSpan(position + 8 + 24, 16));
            }
            else if (chunkId == "data")
            {
                dataOffset = position + 8;
                dataLength = Math.Min(chunkSize, bytes.Length - dataOffset);
                break;
            }

            // RIFF chunks are word-aligned.
            position += 8 + chunkSize + (chunkSize & 1);
        }

        if (dataOffset < 0 || dataLength <= 0)
            throw new InvalidOperationException($"No data chunk found in WAV: {path}");

        var isFloat = audioFormat == 3 || (audioFormat == 0xFFFE && subFormat == IeeeFloatSubtype);
        var sampleFormat = ToSampleFormat(isFloat, bitsPerSample);
        var frames = dataLength / (PcmSampleDecoder.BytesPerSample(sampleFormat) * channels);
        var mono = new float[frames];
        PcmSampleDecoder.DecodeToMono(bytes.AsSpan(dataOffset, dataLength), sampleFormat, channels, mono);

        return sampleRate == targetRate ? mono : Resample(mono, sampleRate, targetRate);
    }

    private static float[] LoadWithMediaFoundation(string path, int targetRate)
    {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException(
                $"Only WAV input is supported on this platform; convert '{Path.GetFileName(path)}' to WAV first.");

        using var reader = new MediaFoundationReader(path);
        var format = reader.WaveFormat;
        using var pcm = new MemoryStream();
        reader.CopyTo(pcm);

        var mono = Convert(pcm.GetBuffer(), (int)pcm.Length, format);
        return format.SampleRate == targetRate ? mono : Resample(mono, format.SampleRate, targetRate);
    }

    private static PcmSampleFormat ToSampleFormat(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                      || (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubtype);
        return ToSampleFormat(isFloat, format.BitsPerSample);
    }

    private static PcmSampleFormat ToSampleFormat(bool isFloat, int bitsPerSample)
    {
        if (isFloat)
        {
            return bitsPerSample == 32
                ? PcmSampleFormat.Float32
                : throw new NotSupportedException($"Unsupported float sample width: {bitsPerSample} bit.");
        }

        return bitsPerSample switch
        {
            8 => PcmSampleFormat.Pcm8,
            16 => PcmSampleFormat.Pcm16,
            24 => PcmSampleFormat.Pcm24,
            32 => PcmSampleFormat.Pcm32,
            _ => throw new NotSupportedException($"Unsupported PCM sample width: {bitsPerSample} bit.")
        };
    }
}
