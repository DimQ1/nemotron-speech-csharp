using NAudio.Wave;
using System.Runtime.InteropServices;

namespace SpeechLib.Audio;

/// <summary>Audio format conversion and resampling utilities for NAudio 3.</summary>
public static class AudioUtils
{
    public static float[] Convert(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var sampleCount = bytesRecorded / bytesPerSample;
        var samples = new float[sampleCount];

        if (format.BitsPerSample == 16)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, bytesRecorded));
            for (var index = 0; index < sampleCount; index++)
                samples[index] = pcm[index] / 32768f;
        }
        else if (format.BitsPerSample == 32)
        {
            var pcm = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRecorded));
            for (var index = 0; index < sampleCount; index++)
                samples[index] = pcm[index];
        }
        else
        {
            for (var index = 0; index < sampleCount; index++)
                samples[index] = (buffer[index] - 128) / 128f;
        }

        if (format.Channels != 2)
            return samples;

        var mono = new float[sampleCount / 2];
        for (var index = 0; index < mono.Length; index++)
            mono[index] = (samples[index * 2] + samples[index * 2 + 1]) * 0.5f;
        return mono;
    }

    public static float[] Resample(float[] samples, int fromRate, int toRate, float gain = 1f)
    {
        if (samples.Length <= 1)
            return [];

        var ratio = (double)fromRate / toRate;
        var result = new float[(int)Math.Ceiling((samples.Length - 1) / ratio)];
        var sampleIndex = 0d;
        var outputIndex = 0;

        while (sampleIndex < samples.Length - 1 && outputIndex < result.Length)
        {
            var index = (int)sampleIndex;
            var fraction = (float)(sampleIndex - index);
            var next = Math.Min(index + 1, samples.Length - 1);
            result[outputIndex++] = (samples[index] * (1f - fraction) + samples[next] * fraction) * gain;
            sampleIndex += ratio;
        }

        return result;
    }

    /// <summary>
    /// Load a WAV file as float32 mono at the target sample rate.
    /// Portable WAV parser (PCM16 / IEEE-float32, mono or stereo) — replaces the
    /// Windows-only <c>AudioFileReader</c> (MediaFoundation) so file loading also
    /// works on Linux. MP3/FLAC input is not supported here; convert to WAV first.
    /// </summary>
    public static float[] LoadFile(string path, int targetRate)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 44)
            throw new InvalidOperationException($"Not a valid WAV file (too small): {path}");

        if (bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F'
            || bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
            throw new InvalidOperationException($"Not a RIFF/WAVE file: {path}");

        int channels = 1;
        int sampleRate = targetRate;
        int bitsPerSample = 16;
        int audioFormat = 1; // 1 = PCM, 3 = IEEE float
        int dataOffset = -1;
        int dataLength = 0;

        var position = 12;
        while (position + 8 <= bytes.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(bytes, position, 4);
            var chunkSize = BitConverter.ToInt32(bytes, position + 4);

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BitConverter.ToInt16(bytes, position + 8);
                channels = BitConverter.ToInt16(bytes, position + 10);
                sampleRate = BitConverter.ToInt32(bytes, position + 12);
                bitsPerSample = BitConverter.ToInt16(bytes, position + 22);
            }
            else if (chunkId == "data")
            {
                dataOffset = position + 8;
                dataLength = chunkSize;
                break;
            }

            // RIFF chunks are word-aligned.
            position += 8 + chunkSize + (chunkSize & 1);
        }

        if (dataOffset < 0 || dataLength <= 0)
            throw new InvalidOperationException($"No data chunk found in WAV: {path}");

        var format = audioFormat == 3
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new WaveFormat(sampleRate, bitsPerSample, channels);

        var data = new byte[dataLength];
        Array.Copy(bytes, dataOffset, data, 0, dataLength);

        var mono = Convert(data, dataLength, format);
        return sampleRate == targetRate ? mono : Resample(mono, sampleRate, targetRate);
    }
}