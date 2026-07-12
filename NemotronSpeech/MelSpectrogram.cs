using System.Numerics;

namespace NemotronSpeech;

/// <summary>
/// Computes mel spectrogram features for Sortformer diarization input.
/// 
/// Parameters (from NVIDIA Sortformer model config):
///   Sample rate:  16000 Hz
///   Window size:  0.025s (400 samples, Hann window)
///   Window stride: 0.01s  (160 samples, 10ms)
///   FFT size:     512
///   Mel bins:     128 (0-8000 Hz)
/// </summary>
internal static class MelSpectrogram
{
    private const int SampleRate = 16000;
    private const double WindowSizeSec = 0.025;
    private const double WindowStrideSec = 0.01;
    private const int FftSize = 512;
    private const int NumMelBins = 128;
    private const double MaxFreq = 8000.0;

    private static readonly int WindowSamples = (int)(SampleRate * WindowSizeSec);
    private static readonly int HopSamples = (int)(SampleRate * WindowStrideSec);
    private static readonly float[] HannWindow = CreateHannWindow(WindowSamples);
    private static readonly float[,] MelFilterbank = CreateMelFilterbank();

    /// <summary>Compute mel spectrogram [NumMelBins, numFrames].</summary>
    public static float[,] Compute(float[] audio)
    {
        int numFrames = Math.Max(1, (audio.Length - WindowSamples) / HopSamples + 1);
        var spec = new float[FftSize / 2 + 1, numFrames];

        for (int f = 0; f < numFrames; f++)
        {
            int start = f * HopSamples;
            var frame = new Complex[FftSize];
            for (int i = 0; i < WindowSamples && start + i < audio.Length; i++)
                frame[i] = new Complex(audio[start + i] * HannWindow[i], 0);

            Fft(frame);

            for (int k = 0; k < FftSize / 2 + 1; k++)
            {
                double mag = frame[k].Magnitude;
                spec[k, f] = (float)(mag * mag);
            }
        }

        var mel = new float[NumMelBins, numFrames];
        for (int m = 0; m < NumMelBins; m++)
            for (int f = 0; f < numFrames; f++)
            {
                float sum = 0;
                for (int k = 0; k < FftSize / 2 + 1; k++)
                    sum += MelFilterbank[m, k] * spec[k, f];
                mel[m, f] = MathF.Log(MathF.Max(sum, 1e-10f));
            }

        return mel;
    }

    private static void Fft(Complex[] buffer)
    {
        int n = buffer.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; j >= bit; bit >>= 1) j -= bit;
            j += bit;
            if (i < j) (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2.0 * Math.PI / len;
            var wlen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (int i = 0; i < n; i += len)
            {
                var w = new Complex(1, 0);
                for (int j = 0; j < len / 2; j++)
                {
                    var u = buffer[i + j];
                    var v = buffer[i + j + len / 2] * w;
                    buffer[i + j] = u + v;
                    buffer[i + j + len / 2] = u - v;
                    w *= wlen;
                }
            }
        }
    }

    private static float[] CreateHannWindow(int size)
    {
        var w = new float[size];
        for (int i = 0; i < size; i++)
            w[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (size - 1)));
        return w;
    }

    private static float[,] CreateMelFilterbank()
    {
        int numSpecBins = FftSize / 2 + 1;
        var fb = new float[NumMelBins, numSpecBins];

        double melMin = HzToMel(0), melMax = HzToMel(MaxFreq);
        double melStep = (melMax - melMin) / (NumMelBins + 1);

        for (int m = 0; m < NumMelBins; m++)
        {
            double melC = melMin + (m + 1) * melStep;
            double melL = melMin + m * melStep, melR = melMin + (m + 2) * melStep;
            int bl = Math.Clamp((int)(MelToHz(melL) / SampleRate * FftSize), 0, numSpecBins - 1);
            int bc = Math.Clamp((int)(MelToHz(melC) / SampleRate * FftSize), 0, numSpecBins - 1);
            int br = Math.Clamp((int)(MelToHz(melR) / SampleRate * FftSize), 0, numSpecBins - 1);
            for (int k = bl; k < bc; k++) fb[m, k] = (float)((double)(k - bl) / (bc - bl));
            if (bc < numSpecBins) fb[m, bc] = 1f;
            for (int k = bc + 1; k <= br && k < numSpecBins; k++) fb[m, k] = (float)((double)(br - k) / (br - bc));
        }
        return fb;
    }

    private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
}
