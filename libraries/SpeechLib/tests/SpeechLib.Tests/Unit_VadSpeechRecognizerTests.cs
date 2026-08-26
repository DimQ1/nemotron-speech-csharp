using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_VadSpeechRecognizerTests
{
    private sealed class FakeVad : IVadFilter
    {
        public bool Speech { get; set; }
        public float LastProbability { get; set; }
        public bool ResetCalled { get; private set; }

        public bool HasSpeech(ReadOnlySpan<float> samples) => Speech;

        public void Reset() => ResetCalled = true;

        public void Dispose() { }
    }

    private sealed class RecordingRecognizer : IStreamingSpeechRecognizer
    {
        public int SampleRate => 16000;
        public int ChunkSamples => 1600;
        public int LastTokenCount { get; private set; }
        public List<float[]> Received { get; } = new();
        public bool FlushCalled { get; private set; }
        public string? FlushResult { get; set; } = "tail";

        public string? ProcessAudio(float[] chunk)
        {
            Received.Add((float[])chunk.Clone());
            LastTokenCount += chunk.Length;
            return null;
        }

        public string? Flush()
        {
            FlushCalled = true;
            return FlushResult;
        }

        public void Dispose() { }
    }

    [Fact]
    public void DisabledVad_PassesAudioThroughVerbatim()
    {
        var vad = new FakeVad { Speech = false };
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad);
        rec.TrySetVad(false);

        var chunk = new float[1600];
        rec.ProcessAudio(chunk);

        Assert.Single(inner.Received);
        Assert.Equal(1600, inner.Received[0].Length);
    }

    [Fact]
    public void EnabledVad_DropsSilenceBeforeSpeech()
    {
        var vad = new FakeVad { Speech = false };
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad);

        rec.ProcessAudio(new float[1600]);

        Assert.Empty(inner.Received); // silence not forwarded
    }

    [Fact]
    public void EnabledVad_ForwardsSpeech()
    {
        var vad = new FakeVad { Speech = true };
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad);

        rec.ProcessAudio(new float[1600]);

        Assert.Single(inner.Received);
    }

    [Fact]
    public void SpeechOnset_FlushesPreSpeechRing()
    {
        var vad = new FakeVad { Speech = false };
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad, preSpeechMs: 250, hangoverMs: 600);

        // 100ms of silence (kept as pre-speech ring).
        rec.ProcessAudio(new float[1600]);
        Assert.Empty(inner.Received);

        // Speech onset: the ring (1600 samples) + the new chunk are forwarded.
        vad.Speech = true;
        rec.ProcessAudio(new float[1600]);

        Assert.Equal(2, inner.Received.Count);
        Assert.Equal(1600, inner.Received[0].Length); // pre-speech ring
        Assert.Equal(1600, inner.Received[1].Length); // current chunk
    }

    [Fact]
    public void ShortSilenceDuringSpeech_KeptAsHangover()
    {
        var vad = new FakeVad { Speech = true };
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad, hangoverMs: 600);

        rec.ProcessAudio(new float[1600]); // speech
        Assert.Single(inner.Received);

        vad.Speech = false;
        rec.ProcessAudio(new float[1600]); // brief silence within hangover

        Assert.Equal(2, inner.Received.Count); // still forwarded
    }

    [Fact]
    public void LongSilence_EndsUtteranceAndResetsVad()
    {
        var vad = new FakeVad { Speech = true };
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad, hangoverMs: 0); // no hangover

        rec.ProcessAudio(new float[1600]); // speech
        Assert.Single(inner.Received);

        vad.Speech = false;
        rec.ProcessAudio(new float[1600]); // silence ends utterance

        Assert.Single(inner.Received); // silence not forwarded
        Assert.True(vad.ResetCalled);  // VAD state reset for next utterance
    }

    [Fact]
    public void Flush_DisabledVad_DelegatesToInner()
    {
        var vad = new FakeVad { Speech = false };
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad);
        rec.TrySetVad(false);

        var result = rec.Flush();

        Assert.True(inner.FlushCalled);
        Assert.Equal("tail", result);
    }

    [Fact]
    public void Flush_EnabledVadWithoutSpeech_DoesNotDelegate()
    {
        var vad = new FakeVad { Speech = false };
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad);

        var result = rec.Flush();

        Assert.False(inner.FlushCalled);
        Assert.Null(result);
    }

    [Fact]
    public void TrySetSearchOptions_DelegatesWhenInnerSupportsIt()
    {
        var vad = new FakeVad();
        var inner = new RecordingRecognizer();
        using var rec = new VadSpeechRecognizer(inner, vad);

        // RecordingRecognizer does not implement IRuntimeConfigurable -> false.
        Assert.False(rec.TrySetSearchOptions(5, 1.0));
    }
}
