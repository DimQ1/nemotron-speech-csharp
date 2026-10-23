using Microsoft.ML.OnnxRuntime;

namespace SpeechLib.Nemotron;

/// <summary>
/// Everything the model carries from one chunk of a stream to the next: the three encoder caches, the mel rows the encoder sees
/// again at the head of the next chunk, the front-end overlap and carried sample, and the committed decoder state with its last
/// token. The stream clock (chunk index, samples fed) is NOT part of it: it belongs to the recognizer, so a model state can be
/// reset or exchanged while the word times and chunk positions of the stream run on.
/// </summary>
/// <remarks>
/// <para>The engine holds one state per branch of the stream (two when the pause-state policy decodes a "keep" and a "fresh"
/// branch side by side, see <see cref="NemotronPauseStateOptions"/>). The encoder caches of the active state are bound to the
/// encoder by reference (no copy, the 6 MB ping-pong pair stays where it is); the small parts are copied into the front-end,
/// the encoder input buffer and the decoder when a state becomes active and back when it stops being active.</para>
/// <para>The list of fields is the one upstream's <c>NemotronSpeechState::ResetStreamingState()</c> touches
/// (<c>src/models/nemotron_speech.cpp</c>), plus the mel overlap that GenAI keeps in its processor.</para>
/// </remarks>
internal sealed class NemotronStreamState : IDisposable
{
    // ping-pong encoder caches: [Input] is bound as the encoder's input, the other one as its output
    private readonly float[][] _channel = new float[2][];
    private readonly float[][] _time = new float[2][];
    private readonly long[][] _length = new long[2][];
    private readonly OrtValue[] _channelValue = new OrtValue[2];
    private readonly OrtValue[] _timeValue = new OrtValue[2];
    private readonly OrtValue[] _lengthValue = new OrtValue[2];

    public NemotronStreamState(long[] channelShape, long[] timeShape, int melCacheFloats, int melOverlap, int decoderStateFloats, int blankId)
    {
        BlankId = blankId;
        var channelSize = checked((int)(channelShape[1] * channelShape[2] * channelShape[3]));
        var timeSize = checked((int)(timeShape[1] * timeShape[2] * timeShape[3]));
        try
        {
            for (var i = 0; i < 2; i++)
            {
                _channel[i] = new float[channelSize];
                _time[i] = new float[timeSize];
                _length[i] = new long[1];
                _channelValue[i] = OrtValue.CreateTensorValueFromMemory(_channel[i], channelShape);
                _timeValue[i] = OrtValue.CreateTensorValueFromMemory(_time[i], timeShape);
                _lengthValue[i] = OrtValue.CreateTensorValueFromMemory(_length[i], [1]);
            }
        }
        catch
        {
            Dispose();
            throw;
        }

        MelCacheRows = new float[melCacheFloats];
        MelOverlap = new float[melOverlap];
        DecoderH = new float[decoderStateFloats];
        DecoderC = new float[decoderStateFloats];
        LastToken = blankId;
    }

    /// <summary>The blank id: the last token of a state that has emitted nothing.</summary>
    public int BlankId { get; }

    /// <summary>Which of the two cache buffers is the encoder's input (0 or 1).</summary>
    public int Input { get; private set; }

    /// <summary>The cache tensors the next encoder run reads.</summary>
    public (OrtValue Channel, OrtValue Time, OrtValue Length) InputCaches => (_channelValue[Input], _timeValue[Input], _lengthValue[Input]);

    /// <summary>The cache tensors the next encoder run writes.</summary>
    public (OrtValue Channel, OrtValue Time, OrtValue Length) OutputCaches => (_channelValue[1 - Input], _timeValue[1 - Input], _lengthValue[1 - Input]);

    /// <summary>A run is done: the caches it wrote are the input of the next one.</summary>
    public void SwapCaches() => Input = 1 - Input;

    /// <summary>The last pre-encode cache rows of the encoder input, saved while another state is active.</summary>
    public float[] MelCacheRows { get; }

    /// <summary>The front-end's pre-emphasised left overlap, saved while another state is active.</summary>
    public float[] MelOverlap { get; }

    /// <summary>The front-end's carried raw sample, saved while another state is active.</summary>
    public float MelPrevious { get; set; }

    /// <summary>The committed LSTM hidden state, saved while another state is active.</summary>
    public float[] DecoderH { get; }

    /// <summary>The committed LSTM cell state, saved while another state is active.</summary>
    public float[] DecoderC { get; }

    /// <summary>The token the prediction network is conditioned on, saved while another state is active.</summary>
    public int LastToken { get; set; }

    /// <summary>The start of a stream: every cache, row and decoder value zero, the blank as the last token.</summary>
    public void Clear()
    {
        for (var i = 0; i < 2; i++)
        {
            Array.Clear(_channel[i]);
            Array.Clear(_time[i]);
            _length[i][0] = 0;
        }
        Input = 0;
        Array.Clear(MelCacheRows);
        Array.Clear(MelOverlap);
        MelPrevious = 0f;
        Array.Clear(DecoderH);
        Array.Clear(DecoderC);
        LastToken = BlankId;
    }

    public void Dispose()
    {
        for (var i = 0; i < 2; i++)
        {
            _channelValue[i]?.Dispose();
            _timeValue[i]?.Dispose();
            _lengthValue[i]?.Dispose();
        }
    }
}
