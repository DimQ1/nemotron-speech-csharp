using Microsoft.ML.OnnxRuntime;

namespace SpeechLib.Nemotron;

/// <summary>
/// ONNX Runtime implementation of <see cref="IRnntStepModel"/>: the prediction network (two-layer LSTM,
/// <c>decoder.onnx</c>) and the joint network (<c>joint.onnx</c>, ends in a log-softmax) on plain ONNX Runtime.
/// </summary>
/// <remarks>
/// Every tensor lives in a buffer allocated once: the sessions write straight into pre-allocated output
/// values, the LSTM state ping-pongs between two buffers (a commit only flips an index) and the prediction
/// output [1, 640, 1] is fed to the joint as [1, 1, 640] over the same memory (a transpose of a length-one axis).
/// The joint takes any number of frames: frames [first, first + n) are copied to the head of one buffer and run as
/// [1, n, hidden] into [1, n, 1, vocab]; there is an input and an output value for every n up to the frames of a chunk,
/// all over the same two buffers.
/// </remarks>
internal sealed class NemotronOnnxStepModel : IRnntStepModel, IDisposable
{
    private readonly InferenceSession _decoder;
    private readonly InferenceSession _joint;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _encoderOutput;     // shared with the engine: [frames, hidden]
    private readonly int _encoderHidden;
    private readonly int _vocabSize;

    // prediction network
    private readonly string[] _decoderInputNames;
    private readonly string[] _decoderOutputNames;
    private readonly long[] _target = new long[1];
    private readonly OrtValue _targetValue;
    private readonly float[][] _h = new float[2][];
    private readonly float[][] _c = new float[2][];
    private readonly OrtValue[] _hValue = new OrtValue[2];
    private readonly OrtValue[] _cValue = new OrtValue[2];
    private readonly float[] _predictionOutput;
    private readonly OrtValue _predictionOutputValue;     // [1, hidden, 1] as the decoder writes it
    private readonly OrtValue _predictionJointValue;      // [1, 1, hidden] as the joint reads it
    private readonly OrtValue[] _decoderInputs = new OrtValue[3];
    private readonly OrtValue[] _decoderOutputs = new OrtValue[3];
    private int _committed;                               // which of the two state buffers is committed

    // joint network; element n - 1 of the value arrays is the view for n frames
    private readonly string[] _jointInputNames;
    private readonly string[] _jointOutputNames;
    private readonly float[] _frames;                     // [frames per chunk, hidden]
    private readonly OrtValue[] _framesValue;             // [1, n, hidden]
    private readonly float[] _logProbs;                   // [frames per chunk, vocab]
    private readonly OrtValue[] _logProbsValue;           // [1, n, 1, vocab]
    private readonly OrtValue[] _jointInputs = new OrtValue[2];
    private readonly OrtValue[] _jointOutputs = new OrtValue[1];

    public NemotronOnnxStepModel(InferenceSession decoder, InferenceSession joint, NemotronModelConfig config, float[] encoderOutput)
    {
        _decoder = decoder;
        _joint = joint;
        _encoderOutput = encoderOutput;
        _encoderHidden = config.Encoder.HiddenSize;

        var d = config.Decoder;
        var j = config.Joiner;
        Require(decoder.InputMetadata, [d.TargetsInput, d.HiddenStateInput, d.CellStateInput], "decoder input");
        Require(decoder.OutputMetadata, [d.Output, d.HiddenStateOutput, d.CellStateOutput], "decoder output");
        Require(joint.InputMetadata, [j.EncoderInput, j.DecoderInput], "joint input");
        Require(joint.OutputMetadata, [j.Output], "joint output");

        _decoderInputNames = [d.TargetsInput, d.HiddenStateInput, d.CellStateInput];
        _decoderOutputNames = [d.Output, d.HiddenStateOutput, d.CellStateOutput];
        _jointInputNames = [j.EncoderInput, j.DecoderInput];
        _jointOutputNames = [j.Output];

        var predictionSize = d.HiddenSize;
        var stateSize = d.NumLayers * 1 * d.HiddenSize;
        long[] stateShape = [d.NumLayers, 1, d.HiddenSize];

        _targetValue = OrtValue.CreateTensorValueFromMemory(_target, [1, 1]);
        for (var i = 0; i < 2; i++)
        {
            _h[i] = new float[stateSize];
            _c[i] = new float[stateSize];
            _hValue[i] = OrtValue.CreateTensorValueFromMemory(_h[i], stateShape);
            _cValue[i] = OrtValue.CreateTensorValueFromMemory(_c[i], stateShape);
        }

        _predictionOutput = new float[predictionSize];
        _predictionOutputValue = OrtValue.CreateTensorValueFromMemory(_predictionOutput, [1, predictionSize, 1]);
        _predictionJointValue = OrtValue.CreateTensorValueFromMemory(_predictionOutput, [1, 1, predictionSize]);

        _vocabSize = config.VocabSize;
        var framesPerChunk = encoderOutput.Length / _encoderHidden;
        _frames = new float[framesPerChunk * _encoderHidden];
        _logProbs = new float[framesPerChunk * _vocabSize];
        _framesValue = new OrtValue[framesPerChunk];
        _logProbsValue = new OrtValue[framesPerChunk];
        for (var n = 1; n <= framesPerChunk; n++)
        {
            _framesValue[n - 1] = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance, _frames.AsMemory(0, n * _encoderHidden), [1, n, _encoderHidden]);
            _logProbsValue[n - 1] = OrtValue.CreateTensorValueFromMemory(
                OrtMemoryInfo.DefaultInstance, _logProbs.AsMemory(0, n * _vocabSize), [1, n, 1, _vocabSize]);
        }
    }

    public void Predict(int token)
    {
        _target[0] = token;
        var pending = 1 - _committed;
        _decoderInputs[0] = _targetValue;
        _decoderInputs[1] = _hValue[_committed];
        _decoderInputs[2] = _cValue[_committed];
        _decoderOutputs[0] = _predictionOutputValue;
        _decoderOutputs[1] = _hValue[pending];
        _decoderOutputs[2] = _cValue[pending];
        _decoder.Run(_runOptions, _decoderInputNames, _decoderInputs, _decoderOutputNames, _decoderOutputs);
    }

    public void CommitPrediction() => _committed = 1 - _committed;

    public ReadOnlySpan<float> JoinFrames(int first, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _framesValue.Length);
        Array.Copy(_encoderOutput, first * _encoderHidden, _frames, 0, count * _encoderHidden);
        _jointInputs[0] = _framesValue[count - 1];
        _jointInputs[1] = _predictionJointValue;
        _jointOutputs[0] = _logProbsValue[count - 1];
        _joint.Run(_runOptions, _jointInputNames, _jointInputs, _jointOutputNames, _jointOutputs);
        return _logProbs.AsSpan(0, count * _vocabSize);
    }

    public void ResetState()
    {
        for (var i = 0; i < 2; i++)
        {
            Array.Clear(_h[i]);
            Array.Clear(_c[i]);
        }
        _committed = 0;
    }

    public void Dispose()
    {
        _targetValue.Dispose();
        foreach (var value in _hValue)
            value.Dispose();
        foreach (var value in _cValue)
            value.Dispose();
        _predictionOutputValue.Dispose();
        _predictionJointValue.Dispose();
        foreach (var value in _framesValue)
            value.Dispose();
        foreach (var value in _logProbsValue)
            value.Dispose();
        _runOptions.Dispose();
    }

    private static void Require(IReadOnlyDictionary<string, NodeMetadata> metadata, string[] names, string what)
    {
        foreach (var name in names)
            if (!metadata.ContainsKey(name))
                throw new InvalidDataException($"The graph has no {what} named \"{name}\" (found: {string.Join(", ", metadata.Keys)}).");
    }
}
