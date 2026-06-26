using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NemotronSpeech;

/// <summary>Audio format conversion utilities.</summary>
public static class AudioUtils
{
    /// <summary>Convert raw byte buffer to float32 mono samples.</summary>
    public static float[] Convert(byte[] buffer, int bytesRecorded, WaveFormat fmt)
    {
        int bps = fmt.BitsPerSample / 8;
        int n = bytesRecorded / bps;
        var samples = new float[n];

        if (fmt.BitsPerSample == 16)
            for (int i = 0; i < n; i++) samples[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
        else if (fmt.BitsPerSample == 32)
            for (int i = 0; i < n; i++) samples[i] = BitConverter.ToSingle(buffer, i * 4);
        else
            for (int i = 0; i < n; i++) samples[i] = (buffer[i] - 128) / 128f;

        if (fmt.Channels == 2)
        {
            var mono = new float[n / 2];
            for (int i = 0; i < mono.Length; i++)
                mono[i] = (samples[i * 2] + samples[i * 2 + 1]) * 0.5f;
            return mono;
        }
        return samples;
    }

    /// <summary>Linear resample to target rate with optional gain.</summary>
    public static List<float> Resample(float[] samples, int fromRate, int toRate, float gain = 1f)
    {
        var result = new List<float>();
        double ratio = (double)fromRate / toRate;
        double si = 0;
        while (si < samples.Length - 1)
        {
            int idx = (int)si;
            float frac = (float)(si - idx);
            int next = Math.Min(idx + 1, samples.Length - 1);
            result.Add((samples[idx] * (1f - frac) + samples[next] * frac) * gain);
            si += ratio;
        }
        return result;
    }

    /// <summary>Load audio file as float32 mono at target sample rate.</summary>
    public static float[] LoadFile(string path, int targetRate)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider source = reader;
        if (reader.WaveFormat.Channels > 1)
            source = new StereoToMonoSampleProvider(source);
        if (reader.WaveFormat.SampleRate != targetRate)
            source = new WdlResamplingSampleProvider(source, targetRate);

        var samples = new List<float>();
        var buf = new float[4096];
        int read;
        while ((read = source.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < read; i++) samples.Add(buf[i]);
        return samples.ToArray();
    }
}
