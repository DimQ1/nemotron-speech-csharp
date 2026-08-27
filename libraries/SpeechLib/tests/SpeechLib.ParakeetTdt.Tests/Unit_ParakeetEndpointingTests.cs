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
        Assert.NotNull(RecognizerType.GetField("_lastEmitSample", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(RecognizerType.GetField("_eouSplits", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(RecognizerType.GetField("_partial", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.NotNull(RecognizerType.GetField("_pendingFinal", BindingFlags.NonPublic | BindingFlags.Instance));
    }

    [Fact]
    public void DecodeFrames_TakesWindowStartSample_ForBlankGapDetection()
    {
        // The decode loop must know the window's absolute sample offset to
        // measure the blank run between emitted tokens across chunk boundaries.
        var method = RecognizerType.GetMethod("DecodeFrames", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.Equal(4, method.GetParameters().Length);
        Assert.Equal("windowStartSample", method.GetParameters()[3].Name);
    }

    [Fact]
    public void StreamingResult_HasFinal_ReflectsNullFinal()
    {
        Assert.True(new StreamingResult("partial", "final").HasFinal);
        Assert.False(new StreamingResult("partial", null).HasFinal);
    }
}
