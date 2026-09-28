using System.Reflection;
using SpeechLib.ParakeetTdt;
using Xunit;

namespace SpeechLib.ParakeetTdt.Tests;

/// <summary>
/// Regression guards for blank-based endpointing and partial/final output on
/// <see cref="ParakeetTdtRecognizer"/>. Decoding itself needs the ONNX model
/// (covered by the E2E verification in tools/converters/ParakeetTdt), so these
/// tests assert the contract and state that the endpointing logic relies on.
/// </summary>
public sealed class Unit_ParakeetEndpointingTests
{
    private static readonly Type RecognizerType = typeof(ParakeetTdtRecognizer);

    [Fact]
    public void Recognizer_ImplementsUtteranceStreamingContract()
    {
        // Blank-based endpointing + partial/final output is exposed through the
        // IUtteranceStreamingRecognizer capability interface.
        Assert.True(typeof(IUtteranceStreamingRecognizer).IsAssignableFrom(RecognizerType));
    }

    [Fact]
    public void Constructor_DefaultStopHistoryEou_Is800ms()
    {
        // Matches NeMo's default stop_history_eou (800 ms of silence closes an
        // utterance) and the prior Silero hangover tuning.
        var ctor = RecognizerType.GetConstructors().Single();
        var eou = ctor.GetParameters().Single(p => p.Name == "stopHistoryEouSeconds");
        Assert.Equal(0.8, eou.DefaultValue);
    }

    [Fact]
    public void EndpointingStateFields_AreDeclared()
    {
        Assert.NotNull(RecognizerType.GetField("_stopEouSamples", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(RecognizerType.GetField("_eouSplits", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(RecognizerType.GetField("_partial", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(RecognizerType.GetField("_pendingFinal", BindingFlags.NonPublic | BindingFlags.Instance));

        // The last-emit position travels with the decoder state so the preview decode
        // (which runs on a copy) cannot disturb committed endpointing.
        var state = RecognizerType.GetNestedType("DecoderState", BindingFlags.NonPublic);
        Assert.NotNull(state);
        Assert.NotNull(state.GetField("LastEmitSample", BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(state.GetMethod("Clone", BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void DecodeFrames_TakesWindowStartSample_ForBlankGapDetection()
    {
        // The decode loop must know the window's absolute sample offset to
        // measure the blank run between emitted tokens across step boundaries,
        // and it advances an explicit decoder state so the preview decode can
        // run on a copy without disturbing the committed state.
        var method = RecognizerType.GetMethod("DecodeFrames", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        var parameters = method.GetParameters();
        Assert.Equal(5, parameters.Length);
        Assert.Equal("windowStartSample", parameters[3].Name);
        Assert.Equal(typeof(long), parameters[3].ParameterType);
        Assert.Equal("state", parameters[4].Name);
    }

    [Fact]
    public void Constructor_DefaultStreamingWindow_IsLowLatency()
    {
        // Real streaming: text shows within a third of a second, words inside an
        // utterance are committed one second behind, the first word two seconds behind.
        var ctor = RecognizerType.GetConstructors().Single();
        var parameters = ctor.GetParameters();
        Assert.Equal(0.32, ParakeetTdtRecognizer.DefaultChunkSeconds);
        Assert.Equal(ParakeetTdtRecognizer.DefaultChunkSeconds, parameters.Single(p => p.Name == "chunkSeconds").DefaultValue);
        Assert.Equal(1.0, parameters.Single(p => p.Name == "rightContextSeconds").DefaultValue);
        Assert.Equal(2.0, parameters.Single(p => p.Name == "onsetContextSeconds").DefaultValue);
        Assert.Equal(true, parameters.Single(p => p.Name == "previewPartials").DefaultValue);
    }

    [Fact]
    public void Recognizer_StartsFreshDecoderStatePerUtterance()
    {
        // After a pause the prediction network is reset (NeMo-style per-utterance
        // decoding): a state carried past a sentence-final token skips the next
        // sentence's onset. Silence context bounds how long token-free audio stays open.
        Assert.NotNull(RecognizerType.GetMethod("StartNewUtteranceState", BindingFlags.NonPublic | BindingFlags.Instance));
        var ctor = RecognizerType.GetConstructors().Single();
        var silence = ctor.GetParameters().Single(p => p.Name == "silenceContextSeconds");
        Assert.Equal(2.0, silence.DefaultValue);
    }

    [Fact]
    public void Recognizer_ExposesRevisablePartialText()
    {
        // PartialText is overridden (not the interface default null) so callers can
        // show the previewed tail after the committed text.
        var property = RecognizerType.GetProperty(nameof(IStreamingSpeechRecognizer.PartialText));
        Assert.NotNull(property);
        Assert.Equal(RecognizerType, property.DeclaringType);
    }

    [Fact]
    public void StreamingResult_HasFinal_ReflectsNullFinal()
    {
        Assert.True(new StreamingResult("partial", "final").HasFinal);
        Assert.False(new StreamingResult("partial", null).HasFinal);
    }
}
