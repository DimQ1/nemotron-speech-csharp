using SpeechLib.Audio;
using Xunit;

namespace SpeechLib.Tests;

public sealed class Unit_RecognizerWarmupTests
{
    /// <summary>Records the order of calls; <c>Clock</c> is the number of samples fed since the last reset.</summary>
    private sealed class RecordingRecognizer : IStreamingSpeechRecognizer
    {
        public int SampleRate => 16000;
        public int ChunkSamples => 8960;
        public List<string> Calls { get; } = new();
        public long Clock { get; private set; }
        public bool FailOnProcess { get; init; }

        public string? ProcessAudio(float[] chunk)
        {
            Calls.Add($"process:{chunk.Length}:{(chunk.All(s => s == 0) ? "silence" : "audio")}");
            if (FailOnProcess)
                throw new InvalidOperationException("no model");
            Clock += chunk.Length;
            return null;
        }

        public string? Flush()
        {
            Calls.Add("flush");
            return null;
        }

        public void ResetStreamingState()
        {
            Calls.Add("reset");
            Clock = 0;
        }

        public void Dispose() { }
    }

    /// <summary>Buffers audio between calls like Parakeet TDT and reports the position of its stream in every result.</summary>
    private sealed class BufferingUtteranceRecognizer : IUtteranceStreamingRecognizer
    {
        public int SampleRate => 16000;
        public int ChunkSamples => 5120;
        public double StopHistoryEouSeconds => 0.8;
        public long BufferedSamples { get; private set; }

        public string? ProcessAudio(float[] chunk) { BufferedSamples += chunk.Length; return null; }
        public StreamingResult ProcessUtterance(float[] chunk) { BufferedSamples += chunk.Length; return new StreamingResult("", null); }
        public StreamingResult FlushUtterance() => new("", null);
        public string? Flush() => null;
        public void ResetStreamingState() => BufferedSamples = 0;
        public void Dispose() { }
    }

    private sealed class OneBatchSource(float[] batch) : IAudioSource
    {
        public int SourceSampleRate => 16000;

        public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
        {
            buffer.Enqueue(batch);
            signal.Set();
        }

        public void Dispose() { }
    }

    [Fact]
    public void Run_ShouldFeedOneSilentChunkAndThenResetTheStream()
    {
        var recognizer = new RecordingRecognizer();

        RecognizerWarmup.Run(recognizer);

        Assert.Equal(["process:8960:silence", "reset"], recognizer.Calls);
        Assert.Equal(0, recognizer.Clock);
    }

    [Fact]
    public void Run_OnAnUtteranceRecognizerThatBuffersAudio_ShouldLeaveNothingOfTheSilentChunkBuffered()
    {
        var recognizer = new BufferingUtteranceRecognizer();

        RecognizerWarmup.Run(recognizer);

        // before: the chunk stayed in the stream (the session began one chunk late for a recognizer that keeps a clock)
        Assert.Equal(0, recognizer.BufferedSamples);
    }

    [Fact]
    public void Run_WhenTheRecognizerFails_ShouldNotThrow()
    {
        var recognizer = new RecordingRecognizer { FailOnProcess = true };

        RecognizerWarmup.Run(recognizer);

        Assert.Equal(["process:8960:silence"], recognizer.Calls);
    }

    [Fact]
    public void LiveTranscriber_ShouldWarmUpBeforeTheFirstBatchAndStartTheStreamAtZero()
    {
        var recognizer = new RecordingRecognizer();
        var batch = Enumerable.Repeat(0.1f, 800).ToArray();

        LiveTranscriber.Run(new OneBatchSource(batch), "test", recognizer);

        // the silent chunk is gone again when the first real batch arrives
        Assert.Equal(["process:8960:silence", "reset", "process:800:audio", "flush"], recognizer.Calls);
        Assert.Equal(800, recognizer.Clock);
    }
}
