namespace SpeechLib.AudioStorage.Tests;

public class Unit_QueuedRecordingAudioWriterTests
{
    [Fact]
    public async Task Append_ShouldCountSamplesImmediatelyAndWriteInBackground()
    {
        using var dir = new TempDir();
        var input = TestAudio.Voice(4);
        var writer = RecordingAudioWriterFactory.Create(dir.File("a"), RecordingAudioFormat.Standard);
        Assert.IsType<QueuedRecordingAudioWriter>(writer);

        TestAudio.AppendInBatches(writer, input);
        Assert.Equal(input.Length, writer.SamplesWritten);
        writer.AddGap(1600);
        Assert.Single(writer.Gaps);

        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        Assert.Equal(dir.File("a.ogg"), result.Path);
        Assert.Equal(input.Length, RecordingAudioReader.ReadMono(result.Path).Length);
        Assert.Equal([new RecordingGap(input.Length, 1600)], result.Gaps);
    }

    [Fact]
    public async Task Append_ShouldNotAliasCallerBuffer()
    {
        using var dir = new TempDir();
        var writer = RecordingAudioWriterFactory.Create(dir.File("b"), RecordingAudioFormat.High);
        var buffer = new float[1600];
        Array.Fill(buffer, 0.25f);
        writer.Append(buffer);
        Array.Clear(buffer); // caller reuses its buffer right away
        var result = await writer.CompleteAsync();
        await writer.DisposeAsync();

        Assert.All(RecordingAudioReader.ReadMono(result.Path), s => Assert.Equal(0.25f, s, 3));
    }

    [Fact]
    public async Task CompleteAsync_ShouldSurfaceBackgroundFailure()
    {
        var inner = new FailingWriter();
        var writer = new QueuedRecordingAudioWriter(inner);
        Exception? faulted = null;
        writer.Faulted += ex => faulted = ex;

        writer.Append(new float[10]); // recognition thread is never blocked or thrown at
        var ex = await Assert.ThrowsAsync<IOException>(() => writer.CompleteAsync());
        Assert.IsType<IOException>(ex.InnerException);
        Assert.NotNull(faulted);
        await writer.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_WithoutComplete_ShouldLeavePartForRecovery()
    {
        using var dir = new TempDir();
        var writer = RecordingAudioWriterFactory.Create(dir.File("c"), RecordingAudioFormat.Standard);
        TestAudio.AppendInBatches(writer, TestAudio.Voice(2.5));
        await writer.DisposeAsync();

        Assert.True(File.Exists(dir.File("c.ogg.part")));
        var recovered = RecordingAudioRecovery.Recover(dir.File("c.ogg.part"));
        // Dispose writes the buffered page, so nothing appended is lost on a clean abort.
        Assert.InRange(recovered.DurationSeconds, 2.4, 2.5);
    }

    private sealed class FailingWriter : IRecordingAudioWriter
    {
        public RecordingAudioFormat Format => RecordingAudioFormat.Standard;
        public int SampleRate => 16000;
        public string FinalPath => "x.ogg";
        public string PartPath => "x.ogg.part";
        public long SamplesWritten => 0;
        public IReadOnlyList<RecordingGap> Gaps => [];
        public void Append(ReadOnlySpan<float> samples) => throw new IOException("disk full");
        public void AddGap(long mutedSamples) { }
        public Task<RecordingAudioResult> CompleteAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("must not be reached");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
