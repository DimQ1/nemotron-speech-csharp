using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SpeechLib.Audio;

/// <summary>Sample container formats a capture device can deliver.</summary>
public enum PcmSampleFormat
{
    /// <summary>Unsigned 8-bit PCM.</summary>
    Pcm8,

    /// <summary>Signed 16-bit little-endian PCM.</summary>
    Pcm16,

    /// <summary>Signed 24-bit little-endian PCM (packed, 3 bytes).</summary>
    Pcm24,

    /// <summary>Signed 32-bit little-endian PCM (also 24-in-32 left-aligned containers).</summary>
    Pcm32,

    /// <summary>IEEE 32-bit float, nominal range −1..1 (WASAPI shared-mode mix format).</summary>
    Float32,
}

/// <summary>Decodes interleaved little-endian PCM frames to mono float samples.</summary>
public static class PcmSampleDecoder
{
    /// <summary>Bytes occupied by one sample of one channel.</summary>
    public static int BytesPerSample(PcmSampleFormat format) => format switch
    {
        PcmSampleFormat.Pcm8 => 1,
        PcmSampleFormat.Pcm16 => 2,
        PcmSampleFormat.Pcm24 => 3,
        PcmSampleFormat.Pcm32 => 4,
        PcmSampleFormat.Float32 => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Decode whole frames from <paramref name="raw"/> and average the channels into
    /// <paramref name="mono"/>. Returns the number of frames decoded. Trailing partial
    /// frames are ignored.
    /// </summary>
    public static int DecodeToMono(ReadOnlySpan<byte> raw, PcmSampleFormat format, int channels, Span<float> mono)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        var bytesPerSample = BytesPerSample(format);
        var frameBytes = bytesPerSample * channels;
        var frames = raw.Length / frameBytes;
        if (frames == 0)
            return 0;
        if (mono.Length < frames)
            throw new ArgumentException("Mono buffer is too small for the decoded frames.", nameof(mono));

        raw = raw.Slice(0, frames * frameBytes);
        switch (format)
        {
            case PcmSampleFormat.Float32:
                Downmix(MemoryMarshal.Cast<byte, float>(raw), channels, 1f, mono);
                break;
            case PcmSampleFormat.Pcm16:
                Downmix(MemoryMarshal.Cast<byte, short>(raw), channels, 1f / 32768f, mono);
                break;
            case PcmSampleFormat.Pcm32:
                Downmix(MemoryMarshal.Cast<byte, int>(raw), channels, 1f / 2147483648f, mono);
                break;
            case PcmSampleFormat.Pcm24:
                DecodePcm24(raw, channels, mono);
                break;
            case PcmSampleFormat.Pcm8:
                DecodePcm8(raw, channels, mono);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return frames;
    }

    private static void Downmix<T>(ReadOnlySpan<T> samples, int channels, float scale, Span<float> mono)
        where T : struct, IConvertible
    {
        var frames = samples.Length / channels;
        var gain = scale / channels;

        if (channels == 1)
        {
            for (var i = 0; i < frames; i++)
                mono[i] = ToFloat(samples[i]) * scale;
            return;
        }

        if (channels == 2)
        {
            for (var i = 0; i < frames; i++)
                mono[i] = (ToFloat(samples[2 * i]) + ToFloat(samples[2 * i + 1])) * gain;
            return;
        }

        for (var i = 0; i < frames; i++)
        {
            float sum = 0f;
            var offset = i * channels;
            for (var c = 0; c < channels; c++)
                sum += ToFloat(samples[offset + c]);
            mono[i] = sum * gain;
        }
    }

    private static float ToFloat<T>(T value) where T : struct, IConvertible
    {
        if (typeof(T) == typeof(float)) return (float)(object)value;
        if (typeof(T) == typeof(short)) return (short)(object)value;
        if (typeof(T) == typeof(int)) return (int)(object)value;
        return value.ToSingle(null);
    }

    private static void DecodePcm24(ReadOnlySpan<byte> raw, int channels, Span<float> mono)
    {
        const float scale = 1f / 8388608f;
        var frameBytes = 3 * channels;
        var frames = raw.Length / frameBytes;
        var gain = scale / channels;

        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            var offset = i * frameBytes;
            for (var c = 0; c < channels; c++)
            {
                var p = offset + c * 3;
                var value = raw[p] | (raw[p + 1] << 8) | (raw[p + 2] << 16);
                if ((value & 0x800000) != 0)
                    value |= unchecked((int)0xFF000000);
                sum += value;
            }
            mono[i] = sum * gain;
        }
    }

    private static void DecodePcm8(ReadOnlySpan<byte> raw, int channels, Span<float> mono)
    {
        var frames = raw.Length / channels;
        var gain = 1f / (128f * channels);

        for (var i = 0; i < frames; i++)
        {
            var sum = 0f;
            var offset = i * channels;
            for (var c = 0; c < channels; c++)
                sum += raw[offset + c] - 128;
            mono[i] = sum * gain;
        }
    }
}
