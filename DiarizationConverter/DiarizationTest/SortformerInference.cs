using DiarizationTest.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DiarizationTest;

/// <summary>
/// ONNX Runtime inference wrapper for the REAL Sortformer diarization model (117.7M params).
/// 
/// Input:  mel spectrogram [1, 128, mel_frames] float32
/// Output: speaker logits    [1, diar_frames, 4]   float32 (sigmoid probabilities)
/// 
/// Audio preprocessing (STFT → mel filterbank) via MelSpectrogram.Compute().
/// Diarization frame rate: 80ms (10ms mel stride / 8× subsampling).
/// </summary>
public sealed class SortformerInference : IDisposable
{
    private readonly InferenceSession _session;
    private readonly int _sampleRate;
    private readonly int _numSpeakers;
    private readonly float _threshold;
    private readonly string _provider;

    /// <summary>Diarization frame stride in seconds (80ms).</summary>
    private const double DiarFrameShiftSeconds = 0.08;
    private const int NumMelBins = 128;

    public SortformerInference(string modelPath, string provider = "cpu",
                                int sampleRate = 16000, int numSpeakers = 4, float threshold = 0.5f)
    {
        _sampleRate = sampleRate;
        _numSpeakers = numSpeakers;
        _threshold = threshold;
        _provider = provider;

        var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_PARALLEL,
            EnableMemoryPattern = true,
            EnableCpuMemArena = true,
        };

        if (provider.Equals("dml", StringComparison.OrdinalIgnoreCase))
        {
            // DirectML GPU acceleration (ORT 1.18+ string-based API)
            sessionOptions.AppendExecutionProvider("DML");
            sessionOptions.IntraOpNumThreads = 1;
            sessionOptions.InterOpNumThreads = 1;
            Console.WriteLine($"  Provider: DirectML (GPU)");
        }
        else if (provider.Equals("dml+cpu", StringComparison.OrdinalIgnoreCase))
        {
            // Hybrid: DML primary, CPU fallback for unsupported ops
            sessionOptions.AppendExecutionProvider("DML");
            sessionOptions.AppendExecutionProvider_CPU();
            sessionOptions.IntraOpNumThreads = 1;
            sessionOptions.InterOpNumThreads = 1;
            Console.WriteLine($"  Provider: DML + CPU fallback");
        }
        else
        {
            sessionOptions.AppendExecutionProvider_CPU();
            sessionOptions.IntraOpNumThreads = Environment.ProcessorCount;
            sessionOptions.InterOpNumThreads = 1;
            Console.WriteLine($"  Provider: CPU ({Environment.ProcessorCount} threads)");
        }

        _session = new InferenceSession(modelPath, sessionOptions);

        Console.WriteLine($"  Model: {Path.GetFileName(modelPath)}");
        if (_session.InputMetadata.Count > 0)
        {
            var inp = _session.InputMetadata.First();
            Console.WriteLine($"  Input:  {inp.Key} [{string.Join(",", inp.Value.Dimensions)}]");
        }
        if (_session.OutputMetadata.Count > 0)
        {
            var outp = _session.OutputMetadata.First();
            Console.WriteLine($"  Output: {outp.Key} [{string.Join(",", outp.Value.Dimensions)}]");
        }
    }

    /// <summary>
    /// Run diarization on a full audio file. Returns speaker segments.
    /// Computes mel spectrogram, runs ONNX inference, converts logits → segments.
    /// </summary>
    public List<SpeakerSegment> Diarize(float[] audio)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 1. Compute mel spectrogram
        float[,] mel = MelSpectrogram.Compute(audio);
        int numMelFrames = mel.GetLength(1);
        Console.Write($"  Mel: [{NumMelBins}×{numMelFrames}] ");

        // 2. Convert to tensor [1, 128, mel_frames]
        var melTensor = new DenseTensor<float>(new[] { 1, NumMelBins, numMelFrames });
        for (int m = 0; m < NumMelBins; m++)
            for (int f = 0; f < numMelFrames; f++)
                melTensor[0, m, f] = mel[m, f];

        // 3. Run inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("processed_signal", melTensor),
        };

        using var results = _session.Run(inputs);
        var outputTensor = results.First().AsTensor<float>();

        int diarFrames = outputTensor.Dimensions[1];
        int numSpks = outputTensor.Dimensions[2];

        // 4. Extract logits
        var logits = new float[diarFrames, numSpks];
        for (int f = 0; f < diarFrames; f++)
            for (int s = 0; s < numSpks; s++)
                logits[f, s] = outputTensor[0, f, s];

        // 5. Logits → segments
        var segments = LogitsToSegments(logits);

        sw.Stop();
        double rtf = sw.Elapsed.TotalSeconds / (audio.Length / (double)_sampleRate);
        Console.WriteLine($"→ [{diarFrames} diar frames] in {sw.Elapsed.TotalSeconds:F2}s ({_provider.ToUpper()} RTF: {rtf:F3})");

        return segments;
    }

    /// <summary>
    /// Convert per-frame sigmoid logits to speaker segments.
    /// Model output is already sigmoid (no softmax needed).
    /// </summary>
    private List<SpeakerSegment> LogitsToSegments(float[,] logits)
    {
        int numFrames = logits.GetLength(0);
        int numSpeakers = logits.GetLength(1);

        // Smoothing (median filter, window=3)
        var smoothed = new float[numFrames, numSpeakers];
        for (int f = 0; f < numFrames; f++)
        {
            for (int s = 0; s < numSpeakers; s++)
            {
                var window = new List<float>();
                for (int w = Math.Max(0, f - 1); w <= Math.Min(numFrames - 1, f + 1); w++)
                    window.Add(logits[w, s]);
                window.Sort();
                smoothed[f, s] = window[window.Count / 2];
            }
        }

        // Best speaker per frame (> threshold)
        var bestSpeaker = new int[numFrames];
        for (int f = 0; f < numFrames; f++)
        {
            int best = -1;
            float bestVal = _threshold;
            for (int s = 0; s < numSpeakers; s++)
            {
                if (smoothed[f, s] > bestVal)
                {
                    bestVal = smoothed[f, s];
                    best = s;
                }
            }
            bestSpeaker[f] = best;
        }

        // Collapse frames → segments (80ms per diar frame)
        var segments = new List<SpeakerSegment>();
        int? currentSpeaker = null;
        int segmentStart = 0;

        for (int f = 0; f < numFrames; f++)
        {
            int spk = bestSpeaker[f];
            if (spk != currentSpeaker)
            {
                if (currentSpeaker.HasValue && currentSpeaker.Value >= 0)
                {
                    double start = segmentStart * DiarFrameShiftSeconds;
                    double end = f * DiarFrameShiftSeconds;
                    if (end - start >= 0.2)
                        segments.Add(new SpeakerSegment(
                            $"SPEAKER_{currentSpeaker.Value:00}",
                            Math.Round(start, 2), Math.Round(end, 2)));
                }
                currentSpeaker = spk >= 0 ? spk : null;
                segmentStart = f;
            }
        }

        if (currentSpeaker.HasValue && currentSpeaker.Value >= 0)
        {
            double start = segmentStart * DiarFrameShiftSeconds;
            double end = numFrames * DiarFrameShiftSeconds;
            if (end - start >= 0.2)
                segments.Add(new SpeakerSegment(
                    $"SPEAKER_{currentSpeaker.Value:00}",
                    Math.Round(start, 2), Math.Round(end, 2)));
        }

        return MergeSegments(segments);
    }

    private static List<SpeakerSegment> MergeSegments(List<SpeakerSegment> segments)
    {
        if (segments.Count <= 1) return segments;
        var merged = new List<SpeakerSegment>();
        var current = segments[0];
        for (int i = 1; i < segments.Count; i++)
        {
            var next = segments[i];
            if (current.SpeakerId == next.SpeakerId && next.StartSeconds - current.EndSeconds < 0.3)
                current = current with { EndSeconds = next.EndSeconds };
            else { merged.Add(current); current = next; }
        }
        merged.Add(current);
        return merged;
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
