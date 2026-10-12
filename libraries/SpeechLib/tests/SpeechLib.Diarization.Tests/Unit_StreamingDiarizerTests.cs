using Xunit.Abstractions;

namespace SpeechLib.Diarization.Tests;

/// <summary>Scheduling, buffering, flushing, cancellation and disposal of the streaming engine, on fake graphs.</summary>
public sealed class Unit_StreamingDiarizerTests(ITestOutputHelper output)
{
    // 20 s: speaker 0 [0,5), silence [5,7), speaker 1 [7,12), speaker 0 [12,20)
    private static float[] Conversation() => FakeEngine.Audio(20,
        (0, 5, 0.5f), (7, 12, -0.5f), (12, 20, 0.5f));

    private static (double S, double E, int Spk)[] Expected =>
        [(0, 5, 0), (7, 12, 1), (12, 20, 0)];

    private static StreamingDiarizer Create(FakeEngine engine, DiarizationPreset preset, PostProcessingOptions? post = null,
        double? retain = null) =>
        new(FakeEngine.CreateModel(engine), new StreamingDiarizerOptions
        {
            Preset = preset,
            PostProcessing = post ?? PostProcessingOptions.Raw,
            MaxRetainedSeconds = retain,
        });

    private static void Feed(IDiarizer d, float[] audio, int block)
    {
        for (int i = 0; i < audio.Length; i += block)
            d.Feed(audio.AsSpan(i, Math.Min(block, audio.Length - i)));
        d.Flush();
    }

    [Theory]
    [InlineData(DiarizationPreset.Offline)]
    [InlineData(DiarizationPreset.LowLatency)]
    [InlineData(DiarizationPreset.VeryLowLatency)]
    [InlineData(DiarizationPreset.UltraLowLatency)]
    [InlineData(DiarizationPreset.NcppV3Streaming)]
    [InlineData(DiarizationPreset.Eco2s)]
    public void Feed_ShouldReproduceTheSpeakerTimelineForEveryPreset(DiarizationPreset preset)
    {
        var engine = new FakeEngine();
        using var d = Create(engine, preset);
        Feed(d, Conversation(), 4000);

        Assert.Equal(2000, d.Timeline.FrameCount);       // floor(N / 160): every frame is scored exactly once
        var turns = d.GetTurns();
        Assert.Equal(Expected.Length, turns.Count);
        for (int i = 0; i < Expected.Length; i++)
        {
            Assert.Equal(Expected[i].Spk, turns[i].Speaker);
            Assert.InRange(turns[i].Start, Expected[i].S - 0.03, Expected[i].S + 0.03);
            Assert.InRange(turns[i].End, Expected[i].E - 0.03, Expected[i].E + 0.03);
        }
        Assert.Empty(d.GetOpenTurns());
    }

    [Fact]
    public void Feed_ShouldGiveTheSameProbabilitiesForAnyBlockSize()
    {
        var audio = Conversation();
        float[]? reference = null;
        foreach (int block in new[] { 16000 * 20, 16000, 4000, 1599, 137 })
        {
            using var d = Create(new FakeEngine(), DiarizationPreset.LowLatency);
            Feed(d, audio, block);
            var m = d.Timeline.ToMatrix();
            reference ??= m;
            Assert.Equal(reference, m);
        }
    }

    [Fact]
    public void Feed_ShouldPublishEachFrameOnceAndAnnounceTheFrontier()
    {
        var engine = new FakeEngine();
        using var d = Create(engine, DiarizationPreset.LowLatency);
        var audio = Conversation();
        long next = 0;
        var all = new List<float>();
        for (int i = 0; i < audio.Length; i += 8000)
        {
            var u = d.Feed(audio.AsSpan(i, 8000));
            Assert.Equal(next, u.FirstFrame);
            next += u.FrameCount;
            all.AddRange(u.Probabilities);
            Assert.Equal(next * 0.01, u.FrontierSeconds, 6);
            Assert.Equal(d.FrontierSeconds, u.FrontierSeconds, 6);
            // a result for time t only appears once (chunk + right context) x 80 ms of audio is buffered
            double fed = (i + 8000) / 16000.0;
            Assert.True(u.FrontierSeconds <= fed + 1e-9);
        }
        var last = d.Flush();
        Assert.Equal(next, last.FirstFrame);
        all.AddRange(last.Probabilities);
        Assert.Equal(d.Timeline.ToMatrix(), all.ToArray());
    }

    [Fact]
    public void Feed_ShouldDelayResultsByTheBufferLatencyOnly()
    {
        var engine = new FakeEngine();
        using var d = Create(engine, DiarizationPreset.LowLatency);
        Assert.Equal(1.04, d.BufferLatencySeconds, 6);
        var audio = Conversation();
        double firstResultAt = -1;
        for (int i = 0; i < audio.Length && firstResultAt < 0; i += 160)
            if (d.Feed(audio.AsSpan(i, 160)).FrameCount > 0) firstResultAt = (i + 160) / 16000.0;
        // The first step needs (9 + 4) x 8 mel frames = 1.04 s plus the front-end's 144 samples of right context.
        Assert.InRange(firstResultAt, 1.04, 1.07);
    }

    [Fact]
    public void Offline_ShouldRunOneStepPerChunkAndFlushTheTail()
    {
        // 70 s = 875 encoder frames: chunks of 340 -> steps at 0, 340, 680
        var engine = new FakeEngine();
        using var d = Create(engine, DiarizationPreset.Offline);
        var audio = FakeEngine.Audio(70, (0, 35, 0.5f), (35, 70, -0.5f));
        d.Feed(audio);
        d.Flush();
        Assert.Equal(7000, d.Timeline.FrameCount);
        Assert.Equal(3, engine.StepCalls);
        // every cache update overflows 264 and compresses back: 264 cache + 40 FIFO frames
        Assert.Equal([(3040, 0), (3040, 304), (1568, 304)], engine.Steps);
        Assert.Equal(2, d.GetTurns().Count);
    }

    [Fact]
    public void Feed_ShouldKeepBuffersBoundedOnLongStreams()
    {
        var engine = new FakeEngine();
        using var d = Create(engine, DiarizationPreset.LowLatency, retain: 30);
        var block = FakeEngine.Audio(10, (0, 5, 0.5f), (5, 10, -0.5f));
        int peakSamples = 0, peakMel = 0;
        for (int i = 0; i < 60 * 10; i++)      // 100 minutes
        {
            d.Feed(block);
            peakSamples = Math.Max(peakSamples, d.BufferedSamples);
            peakMel = Math.Max(peakMel, d.BufferedMelFrames);
        }
        d.Flush();
        Assert.True(peakSamples < 16000 * 12, $"audio buffer reached {peakSamples} samples");
        Assert.True(peakMel < 1500, $"mel buffer reached {peakMel} frames");
        Assert.Equal(600_000, d.Timeline.FrameCount);
        Assert.InRange(d.Timeline.FrameCount - d.Timeline.FirstFrame, 3000, 3000 + 2048);
        Assert.True(engine.MaxCachedFrames <= 264 + 264, $"cache + fifo reached {engine.MaxCachedFrames}");
        Assert.Equal(1200, d.GetTurns().Count);
    }

    [Fact]
    public void Feed_HostWorkOutsideTheGraphsStaysFarBelowRealTime()
    {
        // Mel/pre-emphasis buffering, AOSC update and compression, turn building: everything except ONNX Runtime.
        // Debug builds are several times slower, hence the loose bound.
        var block = FakeEngine.Audio(10, (0, 5, 0.5f), (5, 10, -0.5f));
        foreach (var preset in new[] { DiarizationPreset.LowLatency, DiarizationPreset.UltraLowLatency })
        {
            using var d = Create(new FakeEngine(), preset);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 36; i++) d.Feed(block);   // 6 minutes
            d.Flush();
            double rtf = sw.Elapsed.TotalSeconds / 360;
            output.WriteLine($"{preset}: host-side RTF {rtf:F5} (the fake graphs allocate their outputs too)");
            Assert.True(rtf < 0.05, $"{preset} host-side RTF {rtf}");
        }
    }

    [Fact]
    public void Flush_ShouldHandleShortAndEmptyInput()
    {
        using (var d = Create(new FakeEngine(), DiarizationPreset.LowLatency))
        {
            var u = d.Flush();
            Assert.Equal(0, u.FrameCount);
        }
        foreach (var preset in new[] { DiarizationPreset.LowLatency, DiarizationPreset.Offline })
        {
            using var d = Create(new FakeEngine(), preset);
            d.Feed(FakeEngine.Audio(0.5, (0, 0.5, 0.5f)));
            d.Flush();
            Assert.Equal(50, d.Timeline.FrameCount);
            Assert.Single(d.GetTurns());
        }
        using (var d = Create(new FakeEngine(), DiarizationPreset.LowLatency))
        {
            d.Feed(new float[100]);                    // less than one mel frame
            Assert.Equal(0, d.Flush().FrameCount);
        }
    }

    [Fact]
    public void Flush_ShouldCloseOpenTurnsAndRejectFurtherAudio()
    {
        using var d = Create(new FakeEngine(), DiarizationPreset.LowLatency);
        d.Feed(FakeEngine.Audio(4, (0, 4, 0.5f)));
        Assert.Single(d.GetOpenTurns());
        Assert.Empty(d.GetTurns());
        var u = d.Flush();
        Assert.Single(u.NewTurns);
        Assert.Equal(4.0, u.NewTurns[0].End, 2);
        Assert.Empty(d.GetOpenTurns());
        Assert.Throws<InvalidOperationException>(() => d.Feed(new float[160]));
        Assert.Throws<InvalidOperationException>(() => d.Flush());
    }

    [Fact]
    public void Feed_ShouldReportTurnsAsTheyClose()
    {
        using var d = Create(new FakeEngine(), DiarizationPreset.LowLatency);
        var audio = Conversation();
        var seen = new List<SpeakerTurn>();
        for (int i = 0; i < audio.Length; i += 1600) seen.AddRange(d.Feed(audio.AsSpan(i, 1600)).NewTurns);
        seen.AddRange(d.Flush().NewTurns);
        Assert.Equal(3, seen.Count);
        Assert.Equal(d.GetTurns().OrderBy(t => t.Start), seen.OrderBy(t => t.Start));
    }

    [Fact]
    public void Feed_ShouldStayConsistentAfterCancellation()
    {
        var engine = new FakeEngine();
        using var cts = new CancellationTokenSource();
        engine.OnStep = () => { if (engine.StepCalls == 3) cts.Cancel(); };
        using var d = Create(engine, DiarizationPreset.UltraLowLatency);
        var audio = Conversation();
        Assert.Throws<OperationCanceledException>(() => d.Feed(audio, cts.Token));
        long framesAfterCancel = d.Timeline.FrameCount;
        Assert.InRange(framesAfterCancel, 1, 2000);

        engine.OnStep = null;
        d.Feed(ReadOnlySpan<float>.Empty);              // resumes the pending steps
        d.Flush();
        Assert.Equal(2000, d.Timeline.FrameCount);

        using var reference = Create(new FakeEngine(), DiarizationPreset.UltraLowLatency);
        Feed(reference, audio, audio.Length);
        Assert.Equal(reference.Timeline.ToMatrix(), d.Timeline.ToMatrix());
    }

    [Fact]
    public void Dispose_ShouldReleaseOwnedModelOnlyAndBeIdempotent()
    {
        var shared = new FakeEngine();
        using var model = FakeEngine.CreateModel(shared);
        var d = new StreamingDiarizer(model);
        d.Dispose();
        d.Dispose();
        Assert.False(shared.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => d.Feed(new float[160]));

        model.Dispose();
        model.Dispose();
        Assert.True(shared.IsDisposed);
        using var d2 = new StreamingDiarizer(model);
        Assert.Throws<ObjectDisposedException>(() => d2.Feed(new float[32000]));
    }

    [Fact]
    public async Task Dispose_ShouldWaitForARunningStep()
    {
        var engine = new FakeEngine();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        engine.OnStep = () => { entered.Set(); release.Wait(5000); };
        var model = FakeEngine.CreateModel(engine);
        var d = new StreamingDiarizer(model);
        var worker = Task.Run(() => d.Feed(Conversation()));
        Assert.True(entered.Wait(5000));
        var disposer = Task.Run(model.Dispose);
        await Task.Delay(100);
        Assert.False(disposer.IsCompleted);          // blocked on the in-flight step
        Assert.False(engine.IsDisposed);
        release.Set();
        await disposer.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(engine.IsDisposed);
        try { await worker.WaitAsync(TimeSpan.FromSeconds(5)); } catch (ObjectDisposedException) { /* the feed may hit the disposed model afterwards */ }
    }

    [Fact]
    public void Streams_ShouldShareOneModelIndependently()
    {
        var engine = new FakeEngine();
        using var model = FakeEngine.CreateModel(engine);
        using var a = new StreamingDiarizer(model, new StreamingDiarizerOptions { PostProcessing = PostProcessingOptions.Raw });
        using var b = new StreamingDiarizer(model, new StreamingDiarizerOptions { PostProcessing = PostProcessingOptions.Raw });
        var one = Conversation();
        var two = FakeEngine.Audio(20, (0, 20, -0.5f));
        for (int i = 0; i < one.Length; i += 8000)
        {
            a.Feed(one.AsSpan(i, 8000));
            b.Feed(two.AsSpan(i, 8000));
        }
        a.Flush();
        b.Flush();
        Assert.Equal(3, a.GetTurns().Count);
        Assert.Single(b.GetTurns());
        Assert.Equal(1, b.GetTurns()[0].Speaker);
    }

    [Fact]
    public async Task OfflineDiarizer_ShouldReportProgressAndHonourCancellation()
    {
        var engine = new FakeEngine();
        using var model = FakeEngine.CreateModel(engine);
        using var off = new OfflineDiarizer(model, new OfflineDiarizerOptions
        {
            Preset = DiarizationPreset.LowLatency,
            PostProcessing = PostProcessingOptions.Raw,
            BlockSeconds = 2,
        });
        var audio = Conversation();
        var progress = new List<double>();
        var result = off.Diarize(audio, new SyncProgress(progress));
        Assert.Equal(3, result.Turns.Count);
        Assert.Equal(20.0, result.DurationSeconds, 6);
        Assert.Equal(2, result.SpeakerCount);
        Assert.NotNull(result.Timeline);
        Assert.Equal(2000, result.Timeline!.FrameCount);
        Assert.Contains(1.0, progress);
        Assert.True(progress.Count >= 10);
        Assert.Equal(progress.OrderBy(p => p), progress.OrderBy(p => p).ToList());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => off.Diarize(audio, null, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => off.DiarizeAsync(audio, null, cts.Token));
    }

    [Fact]
    public void OfflineDiarizer_ShouldStreamBlocksAndAWavFile()
    {
        var audio = Conversation();
        using var model = FakeEngine.CreateModel(new FakeEngine());
        using var off = new OfflineDiarizer(model, new OfflineDiarizerOptions { PostProcessing = PostProcessingOptions.Raw, KeepProbabilities = false });

        IEnumerable<ReadOnlyMemory<float>> Blocks()
        {
            for (int i = 0; i < audio.Length; i += 3333) yield return audio.AsMemory(i, Math.Min(3333, audio.Length - i));
        }
        var fromBlocks = off.Diarize(Blocks(), 20);
        Assert.Null(fromBlocks.Timeline);
        Assert.Equal(3, fromBlocks.Turns.Count);

        string path = Path.Combine(Path.GetTempPath(), "diar-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteWav(path, audio);
            var fromWav = off.DiarizeWavFile(path);
            Assert.Equal(20.0, fromWav.DurationSeconds, 3);
            Assert.Equal(fromBlocks.Turns.Count, fromWav.Turns.Count);
            for (int i = 0; i < 3; i++)
            {
                Assert.Equal(fromBlocks.Turns[i].Speaker, fromWav.Turns[i].Speaker);
                Assert.Equal(fromBlocks.Turns[i].Start, fromWav.Turns[i].Start, 1);   // 16-bit quantization only
            }
        }
        finally { File.Delete(path); }
    }

    private sealed class SyncProgress(List<double> sink) : IProgress<double>
    {
        public void Report(double value) => sink.Add(value);
    }

    private static void WriteWav(string path, float[] samples)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + samples.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples.Length * 2);
        foreach (var s in samples) w.Write((short)Math.Round(s * 32767));
    }
}
