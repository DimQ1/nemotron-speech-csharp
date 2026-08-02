using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;
using Xunit;

namespace VoiceType.Tests;

public sealed class Unit_SpeechLibInfrastructureTests
{
    [Fact]
    public void ConcurrentQueueWrapper_WhenFull_DropsOldestBatch()
    {
        var queue = new ConcurrentQueueWrapper(capacity: 2);

        queue.Enqueue([1f]);
        queue.Enqueue([2f]);
        queue.Enqueue([3f]);

        Assert.Equal(2, queue.Count);
        Assert.Equal(1, queue.DroppedBatches);
        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));
        Assert.Single(first);
        Assert.Equal(2f, first[0]);
        Assert.Single(second);
        Assert.Equal(3f, second[0]);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void ConcurrentQueueWrapper_WhenGivenEmptyBatch_DoesNotRetainIt()
    {
        var queue = new ConcurrentQueueWrapper();

        Assert.False(queue.Enqueue([]));
        Assert.True(queue.IsEmpty);
        Assert.Equal(0, queue.Count);
        Assert.Equal(0, queue.DroppedBatches);
    }

    [Fact]
    public void NAudio2AudioSourceFactory_CreatesStableLiveSource()
    {
        var factory = new NAudio2AudioSourceFactory();

        using var source = factory.Create(CaptureMode.Mic, 16000);

        Assert.IsType<BufferedCaptureSource>(source);
    }

    [Fact]
    public void AudioSourceFactories_RejectFileMode()
    {
        var stableFactory = new NAudio2AudioSourceFactory();

        Assert.Throws<InvalidOperationException>(() => stableFactory.Create(CaptureMode.File, 16000));
    }

    [Fact]
    public void CaptureState_CanBeStartedAgainAfterStop()
    {
        using var state = new CaptureState();

        state.Stop();
        state.IsRunning = true;

        Assert.True(state.IsRunning);
    }

    [Fact]
    public void LiveTranscriber_DrainsFinalBatchBeforeFlush()
    {
        var source = new TestAudioSource();
        var recognizer = new TestRecognizer();

        var result = LiveTranscriber.Run(source, "test", recognizer);

        Assert.Equal("42|flush", result);
        Assert.True(source.WasDisposed);
        Assert.True(recognizer.FlushCalled);
    }

    [Fact]
    public async Task CaptureState_Stop_WakesWaitImmediately()
    {
        using var state = new CaptureState();
        var waitTask = Task.Run(() => state.Wait(milliseconds: 5000));

        await Task.Delay(25);
        state.Stop();

        var completed = await Task.WhenAny(waitTask, Task.Delay(500));

        Assert.Same(waitTask, completed);
        Assert.False(state.IsRunning);
    }

    private sealed class TestAudioSource : IAudioSource
    {
        public bool WasDisposed { get; private set; }

        public int SourceSampleRate => 16000;

        public void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, CaptureState state)
        {
            buffer.Enqueue([42]);
            signal.Set();
            state.Stop();
        }

        public void Dispose() => WasDisposed = true;
    }

    private sealed class TestRecognizer : IStreamingSpeechRecognizer
    {
        public bool FlushCalled { get; private set; }

        public int SampleRate => 16000;
        public int ChunkSamples => 1;

        public string? ProcessAudio(float[] chunk) => chunk.Length == 1 && chunk[0] == 42 ? "42" : null;

        public string? Flush()
        {
            FlushCalled = true;
            return "|flush";
        }

        public void Dispose() { }
    }
}