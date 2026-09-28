using System.Buffers.Binary;
using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_PcmSampleDecoderTests
{
    [Fact]
    public void Float32Stereo_AveragesChannels()
    {
        var raw = new byte[2 * 4 * 2];
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(0), 0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(4), -0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(8), 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(raw.AsSpan(12), 0.0f);

        var mono = new float[2];
        var frames = PcmSampleDecoder.DecodeToMono(raw, PcmSampleFormat.Float32, 2, mono);

        Assert.Equal(2, frames);
        Assert.Equal(0f, mono[0], 6);
        Assert.Equal(0.5f, mono[1], 6);
    }

    [Fact]
    public void Pcm16Mono_ScalesToUnitRange()
    {
        var raw = new byte[3 * 2];
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(0), short.MaxValue);
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(2), short.MinValue);
        BinaryPrimitives.WriteInt16LittleEndian(raw.AsSpan(4), 0);

        var mono = new float[3];
        var frames = PcmSampleDecoder.DecodeToMono(raw, PcmSampleFormat.Pcm16, 1, mono);

        Assert.Equal(3, frames);
        Assert.Equal(32767f / 32768f, mono[0], 6);
        Assert.Equal(-1f, mono[1], 6);
        Assert.Equal(0f, mono[2], 6);
    }

    [Fact]
    public void Pcm24_SignExtendsNegativeValues()
    {
        // +8388607 (0x7FFFFF) and -8388608 (0x800000), little-endian packed.
        var raw = new byte[] { 0xFF, 0xFF, 0x7F, 0x00, 0x00, 0x80 };

        var mono = new float[2];
        var frames = PcmSampleDecoder.DecodeToMono(raw, PcmSampleFormat.Pcm24, 1, mono);

        Assert.Equal(2, frames);
        Assert.Equal(8388607f / 8388608f, mono[0], 6);
        Assert.Equal(-1f, mono[1], 6);
    }

    [Fact]
    public void Pcm32_ScalesByFullRange()
    {
        var raw = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(0), int.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), int.MaxValue / 2);

        var mono = new float[2];
        PcmSampleDecoder.DecodeToMono(raw, PcmSampleFormat.Pcm32, 1, mono);

        Assert.Equal(-1f, mono[0], 6);
        Assert.Equal(0.5f, mono[1], 3);
    }

    [Fact]
    public void Pcm8_IsUnsignedCentredAt128()
    {
        var raw = new byte[] { 128, 255, 0 };

        var mono = new float[3];
        PcmSampleDecoder.DecodeToMono(raw, PcmSampleFormat.Pcm8, 1, mono);

        Assert.Equal(0f, mono[0], 6);
        Assert.Equal(127f / 128f, mono[1], 6);
        Assert.Equal(-1f, mono[2], 6);
    }

    [Fact]
    public void TrailingPartialFrame_IsIgnored()
    {
        var raw = new byte[4 * 2 + 3]; // two float32 mono frames + 3 stray bytes
        var mono = new float[2];

        var frames = PcmSampleDecoder.DecodeToMono(raw, PcmSampleFormat.Float32, 1, mono);

        Assert.Equal(2, frames);
    }

    [Fact]
    public void UndersizedMonoBuffer_Throws()
    {
        var raw = new byte[4 * 4];
        Assert.Throws<ArgumentException>(() =>
            PcmSampleDecoder.DecodeToMono(raw, PcmSampleFormat.Float32, 1, new float[2]));
    }
}
