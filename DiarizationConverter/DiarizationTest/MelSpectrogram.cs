using System.Numerics;

namespace DiarizationTest;

/// <summary>
/// Computes mel spectrogram features from raw audio for Sortformer ONNX input.
/// 
/// Parameters (from Sortformer model config):
///   Sample rate:  16000 Hz
///   Window size:  0.025s (400 samples)
///   Window stride: 0.01s  (160 samples)  
///   FFT size:     512
///   Mel bins:     128
///   Window:       Hann
/// </summary>
public static class MelSpectrogram
{
    private const int SampleRate = 16000;
    private const double WindowSizeSec = 0.025;
    private const double WindowStrideSec = 0.01;
    private const int FftSize = 512;
    private const int NumMelBins = 128;
    private const double MinFreq = 0.0;
    private const double MaxFreq = 8000.0;

    private static readonly int WindowSamples = (int)(SampleRate * WindowSizeSec);
    private static readonly int HopSamples = (int)(SampleRate * WindowStrideSec);

    // Precomputed Hann window
    private static readonly float[] HannWindow = CreateHannWindow(WindowSamples);

    // Precomputed mel filterbank [NumMelBins, FftSize/2+1]
    private static readonly float[,] MelFilterbank = CreateMelFilterbank();

    /// <summary>
    /// Compute mel spectrogram from raw audio samples.
    /// </summary>
    /// <param name="audio">16kHz mono float audio samples</param>
    /// <returns>Mel spectrogram [NumMelBins, numFrames]</returns>
    public static float[,] Compute(float[] audio)
    {
        int numFrames = (audio.Length - WindowSamples) / HopSamples + 1;
        if (numFrames < 1) numFrames = 1;

        // Power spectrogram [FftSize/2+1, numFrames]
        var spec = new float[FftSize / 2 + 1, numFrames];

        for (int f = 0; f < numFrames; f++)
        {
            int start = f * HopSamples;

            // Extract windowed frame
            var frame = new Complex[FftSize];
            for (int i = 0; i < WindowSamples && start + i < audio.Length; i++)
                frame[i] = new Complex(audio[start + i] * HannWindow[i], 0);
            // Rest is zero-padded

            // FFT
            Fft(frame);

            // Power spectrum (|X|^2)
            for (int k = 0; k < FftSize / 2 + 1; k++)
            {
                double mag = frame[k].Magnitude;
                spec[k, f] = (float)(mag * mag);
            }
        }

        // Apply mel filterbank [NumMelBins, FftSize/2+1] × [FftSize/2+1, numFrames] = [NumMelBins, numFrames]
        var mel = new float[NumMelBins, numFrames];
        for (int m = 0; m < NumMelBins; m++)
        {
            for (int f = 0; f < numFrames; f++)
            {
                float sum = 0;
                for (int k = 0; k < FftSize / 2 + 1; k++)
                    sum += MelFilterbank[m, k] * spec[k, f];
                // Log-mel (add small epsilon to avoid log(0))
                mel[m, f] = MathF.Log(MathF.Max(sum, 1e-10f));
            }
        }

        return mel;
    }

    /// <summary>
    /// Convert mel spectrogram to ONNX input tensor shape [1, NumMelBins, numFrames].
    /// </summary>
    public static float[,,] ToTensorShape(float[,] mel)
    {
        int melBins = mel.GetLength(0);
        int numFrames = mel.GetLength(1);
        var tensor = new float[1, melBins, numFrames];
        for (int m = 0; m < melBins; m++)
            for (int f = 0; f < numFrames; f++)
                tensor[0, m, f] = mel[m, f];
        return tensor;
    }

    /// <summary>
    /// In-place radix-2 FFT (Cooley-Tukey, decimation-in-time).
    /// </summary>
    private static void Fft(Complex[] buffer)
    {
        int n = buffer.Length;

        // Bit-reversal permutation
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; j >= bit; bit >>= 1)
                j -= bit;
            j += bit;
            if (i < j)
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        // FFT butterfly
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
        var window = new float[size];
        for (int i = 0; i < size; i++)
            window[i] = 0.5f * (1.0f - MathF.Cos(2.0f * MathF.PI * i / (size - 1)));
        return window;
    }

    /// <summary>
    /// Create mel filterbank matrix [NumMelBins, FftSize/2+1].
    /// Converts linear frequency bins to mel-scale triangular filters.
    /// </summary>
    private static float[,] CreateMelFilterbank()
    {
        int numSpecBins = FftSize / 2 + 1;
        var fb = new float[NumMelBins, numSpecBins];

        double melMin = HzToMel(MinFreq);
        double melMax = HzToMel(MaxFreq);
        double melStep = (melMax - melMin) / (NumMelBins + 1);

        for (int m = 0; m < NumMelBins; m++)
        {
            double melCenter = melMin + (m + 1) * melStep;
            double melLeft = melMin + m * melStep;
            double melRight = melMin + (m + 2) * melStep;

            double freqCenter = MelToHz(melCenter);
            double freqLeft = MelToHz(melLeft);
            double freqRight = MelToHz(melRight);

            int binLeft = (int)Math.Floor(freqLeft / SampleRate * FftSize);
            int binCenter = (int)Math.Round(freqCenter / SampleRate * FftSize);
            int binRight = (int)Math.Ceiling(freqRight / SampleRate * FftSize);

            binLeft = Math.Clamp(binLeft, 0, numSpecBins - 1);
            binCenter = Math.Clamp(binCenter, 0, numSpecBins - 1);
            binRight = Math.Clamp(binRight, 0, numSpecBins - 1);

            // Rising slope
            for (int k = binLeft; k < binCenter; k++)
                fb[m, k] = (float)((double)(k - binLeft) / (binCenter - binLeft));
            // Peak
            if (binCenter < numSpecBins)
                fb[m, binCenter] = 1.0f;
            // Falling slope
            for (int k = binCenter + 1; k <= binRight && k < numSpecBins; k++)
                fb[m, k] = (float)((double)(binRight - k) / (binRight - binCenter));
        }

        return fb;
    }

    private static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
    private static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
}
