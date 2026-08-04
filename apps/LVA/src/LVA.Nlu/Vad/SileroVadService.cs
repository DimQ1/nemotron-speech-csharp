using LVA.Core.Configuration;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LVA.Nlu.Vad;

/// <summary>
/// Silero VAD (ONNX, v5 layout: inputs input/state/sr, state [2,B,128]).
/// Processes 30 ms frames @16 kHz with persistent recurrent state.
/// Singleton session; no blocking calls on the audio thread beyond inference (~1 ms).
/// </summary>
public sealed class SileroVadService : IVadService
{
    public const int FrameSamples = 480;      // 30 ms @ 16 kHz
    public const int SampleRate = 16000;
    private const int StateSize = 128;

    private readonly InferenceSession _session;
    private readonly VadOptions _options;
    private float[,,] _state = new float[2, 1, StateSize];
    private int _silenceFrames;
    private bool _disposed;

    public event Action? SpeechStarted;
    public event Action<float>? SpeechFrame;
    public event Action<TimeSpan>? SpeechEnded;

    public bool IsSpeechActive { get; private set; }

    public SileroVadService(VadOptions options)
    {
        _options = options;
        var so = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _session = new InferenceSession(options.ModelPath, so);
    }

    public void AcceptFrame(ReadOnlySpan<float> samples)
    {
        if (samples.Length != FrameSamples)
            throw new ArgumentException($"Frame must be {FrameSamples} samples (30 ms @16 kHz).", nameof(samples));

        var input = new DenseTensor<float>(new[] { 1, FrameSamples });
        samples.CopyTo(input.Buffer.Span);

        var stateTensor = new DenseTensor<float>(new[] { 2, 1, StateSize });
        Buffer.BlockCopy(_state, 0, stateTensor.Buffer.Span.ToArray(), 0, 2 * StateSize * sizeof(float));

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", input),
            NamedOnnxValue.CreateFromTensor("state", stateTensor),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>(new long[] { SampleRate }, new[] { 1 }))
        };

        using var results = _session.Run(inputs);
        float prob = 0f;
        foreach (var r in results)
        {
            if (r.Name == "output")
                prob = r.AsEnumerable<float>().First();
            else if (r.Name == "stateN")
            {
                var t = r.AsTensor<float>();
                Buffer.BlockCopy(t.ToArray(), 0, _state, 0, 2 * StateSize * sizeof(float));
            }
        }

        SpeechFrame?.Invoke(prob);
        UpdateSpeechState(prob);
    }

    private void UpdateSpeechState(float prob)
    {
        if (!IsSpeechActive)
        {
            if (prob >= _options.Threshold)
            {
                IsSpeechActive = true;
                _silenceFrames = 0;
                SpeechStarted?.Invoke();
            }
        }
        else
        {
            if (prob < _options.Threshold)
            {
                _silenceFrames++;
                if (_silenceFrames * 30 >= _options.MinSilenceMs)
                {
                    IsSpeechActive = false;
                    var duration = TimeSpan.FromMilliseconds(_silenceFrames * 30);
                    _silenceFrames = 0;
                    SpeechEnded?.Invoke(duration);
                }
            }
            else
            {
                _silenceFrames = 0;
            }
        }
    }

    public void Reset()
    {
        Array.Clear(_state);
        IsSpeechActive = false;
        _silenceFrames = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
