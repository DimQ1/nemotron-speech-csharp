namespace SpeechLib.AudioStorage.Tests;

internal static class TestAudio
{
    /// <summary>Speech-like test signal: a gliding harmonic tone with a syllable-rate envelope.</summary>
    public static float[] Voice(double seconds, int sampleRate = 16000)
    {
        var samples = new float[(int)(seconds * sampleRate)];
        double phase = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / sampleRate;
            double f0 = 140 + 40 * Math.Sin(2 * Math.PI * 0.7 * t);
            phase += 2 * Math.PI * f0 / sampleRate;
            double envelope = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 4 * t);
            double v = 0;
            for (int h = 1; h <= 8; h++)
                v += Math.Sin(h * phase) / h;
            samples[i] = (float)(0.25 * envelope * v);
        }
        return samples;
    }

    public static double SnrDb(ReadOnlySpan<float> reference, ReadOnlySpan<float> decoded)
    {
        int n = Math.Min(reference.Length, decoded.Length);
        double sig = 0, err = 0;
        for (int i = 0; i < n; i++)
        {
            sig += reference[i] * (double)reference[i];
            double d = reference[i] - decoded[i];
            err += d * d;
        }
        return 10 * Math.Log10(sig / Math.Max(err, 1e-12));
    }

    public static void AppendInBatches(IRecordingAudioWriter writer, float[] samples, int batch = 1600)
    {
        for (int i = 0; i < samples.Length; i += batch)
            writer.Append(samples.AsSpan(i, Math.Min(batch, samples.Length - i)));
    }
}

/// <summary>A fresh temp directory per test, deleted afterwards.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "speechlib-audiostorage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
