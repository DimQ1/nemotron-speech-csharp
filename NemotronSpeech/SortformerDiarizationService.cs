using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SpeechLib;
using SpeechLib.Models;

namespace NemotronSpeech;

/// <summary>
/// Sortformer ONNX speaker diarization service.
/// Uses NVIDIA SortformerEncLabelModel exported to ONNX (117.7M params).
/// 
/// Input:  mel spectrogram [1, 128, mel_frames] float32
/// Output: speaker logits    [1, diar_frames, 4]   float32 (sigmoid)
/// 
/// Diarization frame rate: 80ms (10ms mel stride / 8× encoder subsampling).
/// </summary>
public sealed class SortformerDiarizationService : IDiarizationService
{
    private readonly InferenceSession _session;
    private readonly float _threshold;

    private const double DiarFrameShiftSec = 0.08;
    private const int NumMelBins = 128;
    private const int NumSpeakers = 4;

    public int SampleRate => 16000;

    /// <summary>
    /// Create a Sortformer diarization service.
    /// </summary>
    /// <param name="modelPath">Path to sortformer.onnx file.</param>
    /// <param name="threshold">Speaker probability threshold (default 0.5).</param>
    public SortformerDiarizationService(string modelPath, float threshold = 0.5f)
    {
        _threshold = threshold;

        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Diarization model not found: {modelPath}");

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Environment.ProcessorCount,
            InterOpNumThreads = 1,
        };
        options.AppendExecutionProvider_CPU();

        _session = new InferenceSession(modelPath, options);
    }

    public List<DiarizationSegment> Diarize(float[] audio)
    {
        // 1. Compute mel spectrogram [128, melFrames]
        float[,] mel = MelSpectrogram.Compute(audio);
        int numMelFrames = mel.GetLength(1);

        // 2. Tensor [1, 128, melFrames]
        var melTensor = new DenseTensor<float>(new[] { 1, NumMelBins, numMelFrames });
        for (int m = 0; m < NumMelBins; m++)
            for (int f = 0; f < numMelFrames; f++)
                melTensor[0, m, f] = mel[m, f];

        // 3. ONNX inference
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("processed_signal", melTensor),
        };

        using var results = _session.Run(inputs);
        var output = results.First().AsTensor<float>();

        int diarFrames = output.Dimensions[1];
        int numSpks = output.Dimensions[2];

        // 4. Extract logits [diarFrames, numSpks]
        var logits = new float[diarFrames, numSpks];
        for (int f = 0; f < diarFrames; f++)
            for (int s = 0; s < numSpks; s++)
                logits[f, s] = output[0, f, s];

        // 5. Logits → speaker segments
        return LogitsToSegments(logits, diarFrames);
    }

    private List<DiarizationSegment> LogitsToSegments(float[,] logits, int numFrames)
    {
        // Median smoothing (window=3)
        var smoothed = new float[numFrames, NumSpeakers];
        for (int f = 0; f < numFrames; f++)
        {
            for (int s = 0; s < NumSpeakers; s++)
            {
                var window = new List<float>();
                for (int w = Math.Max(0, f - 1); w <= Math.Min(numFrames - 1, f + 1); w++)
                    window.Add(logits[w, s]);
                window.Sort();
                smoothed[f, s] = window[window.Count / 2];
            }
        }

        // Best speaker per frame
        var bestSpk = new int[numFrames];
        for (int f = 0; f < numFrames; f++)
        {
            int best = -1;
            float bestVal = _threshold;
            for (int s = 0; s < NumSpeakers; s++)
            {
                if (smoothed[f, s] > bestVal) { bestVal = smoothed[f, s]; best = s; }
            }
            bestSpk[f] = best;
        }

        // Collapse frames → segments
        var segments = new List<DiarizationSegment>();
        int? current = null;
        int segStart = 0;

        for (int f = 0; f < numFrames; f++)
        {
            if (bestSpk[f] != current)
            {
                if (current.HasValue && current >= 0)
                {
                    double start = segStart * DiarFrameShiftSec;
                    double end = f * DiarFrameShiftSec;
                    if (end - start >= 0.2)
                        segments.Add(new DiarizationSegment
                        {
                            SpeakerId = $"SPEAKER_{current.Value:00}",
                            StartSeconds = Math.Round(start, 2),
                            EndSeconds = Math.Round(end, 2)
                        });
                }
                current = bestSpk[f] >= 0 ? bestSpk[f] : null;
                segStart = f;
            }
        }

        if (current.HasValue && current >= 0)
        {
            double start = segStart * DiarFrameShiftSec;
            double end = numFrames * DiarFrameShiftSec;
            if (end - start >= 0.2)
                segments.Add(new DiarizationSegment
                {
                    SpeakerId = $"SPEAKER_{current.Value:00}",
                    StartSeconds = Math.Round(start, 2),
                    EndSeconds = Math.Round(end, 2)
                });
        }

        // Merge adjacent same-speaker segments (gap < 0.3s)
        return MergeSegments(segments);
    }

    private static List<DiarizationSegment> MergeSegments(List<DiarizationSegment> segs)
    {
        if (segs.Count <= 1) return segs;
        var merged = new List<DiarizationSegment>();
        var cur = segs[0];
        for (int i = 1; i < segs.Count; i++)
        {
            var nxt = segs[i];
            if (cur.SpeakerId == nxt.SpeakerId && nxt.StartSeconds - cur.EndSeconds < 0.3)
                cur = cur with { EndSeconds = nxt.EndSeconds };
            else { merged.Add(cur); cur = nxt; }
        }
        merged.Add(cur);
        return merged;
    }

    public void Dispose() => _session.Dispose();
}
