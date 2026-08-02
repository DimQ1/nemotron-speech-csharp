using NAudio.Wave;
using NAudio.Wave.SampleProviders;
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

    public static float[] LoadFile(string path, int targetRate)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader;
        if (reader.WaveFormat.Channels > 1)
            source = new StereoToMonoSampleProvider(source);
        if (reader.WaveFormat.SampleRate != targetRate)
            source = new WdlResamplingSampleProvider(source, targetRate);

        var samples = new List<float>();
        var readBuffer = new float[4096];
        int read;
        while ((read = source.Read(readBuffer.AsSpan())) > 0)
        {
            for (var index = 0; index < read; index++)
                samples.Add(readBuffer[index]);
        }

        return samples.ToArray();
    }
}