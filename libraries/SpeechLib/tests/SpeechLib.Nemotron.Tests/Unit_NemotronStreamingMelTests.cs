using System.IO.Compression;
using SpeechLib.Nemotron;
using Xunit;

namespace SpeechLib.Nemotron.Tests;

public sealed class Unit_NemotronStreamingMelTests
{
    private const int Mels = 128;

    private static NemotronStreamingMel NewMel() => new(NemotronTestEnvironment.LoadConfig("int4-c056"));

    [Fact]
    public void Filterbank_ShouldMatchTheLibrosaSlaneyGoldenVector()
    {
        var golden = ReadFloatsGz(NemotronTestEnvironment.DataPath("mel", "mel_filterbank_128x257.bin.gz"));
        var filterbank = NemotronStreamingMel.CreateMelFilterbank(128, 512, 16000);

        Assert.Equal(golden.Length, filterbank.Length);
        var maxDiff = golden.Zip(filterbank, (g, f) => Math.Abs(g - f)).Max();
        Assert.True(maxDiff < 1e-6, $"max |diff| = {maxDiff:E2}");
        // Slaney area normalisation: the triangles get taller as they get narrower, none is empty.
        for (var m = 0; m < Mels; m++)
            Assert.Contains(Enumerable.Range(0, 257), k => filterbank[m * 257 + k] > 0f);
    }

    [Fact]
    public void Window_ShouldBeTheSymmetricHann()
    {
        var window = NewMel().WindowCopy();

        Assert.Equal(400, window.Length);
        Assert.Equal(0f, window[0], 1e-7);
        Assert.Equal(0f, window[399], 1e-6);                 // symmetric: ends at zero, not at 1 - cos(2*pi*399/400)
        Assert.Equal(window[1], window[398], 1e-6);
        Assert.Equal(1f, window[199] * 0.5f + window[200] * 0.5f, 1e-3);
    }

    [Fact]
    public void Geometry_ShouldFollowTheShippedChunkSizes()
    {
        var mel = NewMel();

        Assert.Equal(56, mel.FramesFor(8960));
        Assert.Equal(112, mel.FramesFor(17920));
        Assert.Equal(40, mel.LookaheadFor(8960));   // the last frame's window reaches 40 samples past the chunk
        Assert.Equal(40, mel.LookaheadFor(17920));
        Assert.Equal(128, mel.NumMels);
    }

    // Where the front-end's math comes from (the bit-exact variant is gated by [WindowsX64Theory]). The goldens are the float tensors GenAI 0.16.0 produced on Windows x64
    // (UCRT logf/cosf); the front-end calls the platform's MathF.Log/Cos/Exp, so only that platform can reproduce them to the
    // last bit. Measured on Linux x64 (glibc 2.39, .NET 10): 6 to 12 percent of the mel floats of the three clips below
    // differ, by at most 7e-5 (73 ulp in the quiet bins); the tokens and text of 8 INT4 c056 clips stayed identical to
    // the goldens (R3 review, I4b notes). arm64, macOS, Android and iOS are not measured.
    // 2e-4 = about 3x the largest difference measured on Linux; a real regression (clean mode, a wrong window, a
    // dropped pre-emphasis) moves quiet bins by whole nats.
    private const double PortableTolerance = 2e-4;

    [Theory]
    [InlineData("en0001", 8960)]
    [InlineData("en0001", 17920)]
    [InlineData("ru0001", 8960)]
    public void Process_ChunksOfAClip_ShouldMatchTheMelGenAiFeedsTheEncoderWithinTheLibmTolerance(string clip, int chunkSamples)
    {
        // Runs on every platform: same dumps as the bit-exact test below, but a bound instead of equality.
        var worst = CompareWithGenAiDumps(clip, chunkSamples, (expected, actual, where) =>
        {
            var diff = Math.Abs((double)expected - actual);
            Assert.True(diff <= PortableTolerance, $"{where}: GenAI {expected:R}, got {actual:R} (|diff| {diff:E2} > {PortableTolerance:E1})");
        });
        Assert.True(worst <= PortableTolerance);
    }

    [WindowsX64Theory]
    [InlineData("en0001", 8960)]
    [InlineData("en0001", 17920)]
    [InlineData("ru0001", 8960)]
    public void Process_ChunksOfAClip_ShouldBeBitIdenticalToTheMelGenAiFeedsTheEncoder_OnWindowsX64(string clip, int chunkSamples)
    {
        // genai_mel_*.bin: the audio_features tensors [1, 9 + M, 128] that GenAI 0.16.0's StreamingProcessor produced for
        // this audio on Windows x64 (dumped through the C API), every chunk including the zero-padded flush chunk.
        CompareWithGenAiDumps(clip, chunkSamples, (expected, actual, where) =>
            Assert.True(BitConverter.SingleToInt32Bits(expected) == BitConverter.SingleToInt32Bits(actual),
                $"{where}: GenAI {expected:R}, got {actual:R}"));
    }

    // Feeds the clip chunk by chunk and hands every (GenAI value, our value) pair to the check; returns the largest |diff|.
    private static double CompareWithGenAiDumps(string clip, int chunkSamples, Action<float, float, string> check)
    {
        var audio = LoadSlice(clip);
        var mel = NewMel();
        var frames = mel.FramesFor(chunkSamples);
        var rows = 9 + frames;
        var genai = ReadFloats(NemotronTestEnvironment.DataPath("mel", $"genai_mel_{clip}_N{chunkSamples}.bin"));
        var chunks = (audio.Length + chunkSamples - 1) / chunkSamples;
        Assert.Equal(chunks * rows * Mels, genai.Length);

        var worst = 0.0;
        var output = new float[frames * Mels];
        for (var k = 0; k < chunks; k++)
        {
            var chunk = new float[chunkSamples];                    // the last, partial chunk is zero padded like Flush()
            Array.Copy(audio, k * chunkSamples, chunk, 0, Math.Min(chunkSamples, audio.Length - k * chunkSamples));
            Assert.Equal(frames, mel.Process(chunk, default, output));

            var expected = genai.AsSpan(k * rows * Mels + 9 * Mels, frames * Mels);
            for (var i = 0; i < output.Length; i++)
            {
                check(expected[i], output[i], $"chunk {k}, frame {i / Mels}, bin {i % Mels}");
                worst = Math.Max(worst, Math.Abs((double)expected[i] - output[i]));
            }

            // the 9 cache rows in front: zeros for the first chunk, else the last 9 frames of the previous chunk
            // (both are GenAI's own values, so this holds on every platform)
            var cache = genai.AsSpan(k * rows * Mels, 9 * Mels);
            if (k == 0)
                Assert.All(cache.ToArray(), v => Assert.Equal(0f, v));
            else
                Assert.True(cache.SequenceEqual(genai.AsSpan((k - 1) * rows * Mels + (rows - 9) * Mels, 9 * Mels)));
        }
        return worst;
    }

    [Fact]
    public void Process_AfterReset_ShouldReproduceTheFirstRun()
    {
        var audio = LoadSlice("en0001");
        var mel = NewMel();
        var first = new float[56 * Mels];
        var again = new float[56 * Mels];

        mel.Process(audio.AsSpan(0, 8960), default, first);
        mel.Process(audio.AsSpan(8960, 8960), default, again);   // advance the stream state
        mel.Reset();
        mel.Process(audio.AsSpan(0, 8960), default, again);

        Assert.Equal(first, again);
    }

    [Fact]
    public void Process_SteadyState_ShouldNotAllocate()
    {
        var audio = LoadSlice("en0001");
        var mel = NewMel();
        var output = new float[56 * Mels];
        mel.Process(audio.AsSpan(0, 8960), default, output);
        mel.Process(audio.AsSpan(0, 8960), default, output);

        // Tiered JIT / on-stack replacement can allocate on this thread while the loop is recompiled (6848 bytes once on a
        // Windows runner), so steady state means one window of calls without an allocation; a per-call allocation in
        // Process shows up in every window.
        var allocated = long.MaxValue;
        for (var window = 0; window < 5 && allocated != 0; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 5; i++)
                mel.Process(audio.AsSpan(0, 8960), default, output);
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Process_FaithfulMode_ShouldAppendZerosSoTheLastFrameDeviatesFromNeMo()
    {
        var audio = LoadSlice("en0001");
        var mel = NewMel();
        var output = new float[56 * Mels];
        mel.Process(audio.AsSpan(0, 8960), default, output);
        mel.Process(audio.AsSpan(8960, 8960), default, output);   // chunk 1: speech, so the missing 40 samples matter

        // Frame 55 of chunk 1 (global frame 111) has a window that runs 40 samples past the chunk. GenAI's extractor
        // sees zeros there (in the pre-emphasised domain), the offline NeMo mel sees the next samples.
        var y = PreEmphasize(audio);
        var withZeros = y.Take(2 * 8960).ToArray();
        var filterbank = NemotronStreamingMel.CreateMelFilterbank(128, 512, 16000);

        var faithful = ReferenceFrame(withZeros, 111, filterbank);
        var nemo = ReferenceFrame(y, 111, filterbank);
        var last = output.AsSpan(55 * Mels, Mels).ToArray();

        Assert.True(MaxDiff(last, faithful) < 5e-4, $"faithful frame differs from its zero-tail reference by {MaxDiff(last, faithful):E2}");
        Assert.True(MaxDiff(last, nemo) > 0.5, $"the last frame should deviate from NeMo by several nats in quiet bins, got {MaxDiff(last, nemo):F3}");

        // every other frame agrees with NeMo
        for (var t = 0; t < 55; t += 9)
            Assert.True(MaxDiff(output.AsSpan(t * Mels, Mels).ToArray(), ReferenceFrame(y, 56 + t, filterbank)) < 5e-4, $"frame {t}");
    }

    [Fact]
    public void Process_CleanMode_ShouldGiveTheLastFrameItsRealWindow()
    {
        var audio = LoadSlice("en0001");
        var mel = NewMel();
        var filterbank = NemotronStreamingMel.CreateMelFilterbank(128, 512, 16000);
        var y = PreEmphasize(audio);
        var output = new float[56 * Mels];

        for (var k = 0; k < 2; k++)
        {
            var chunk = audio.AsSpan(k * 8960, 8960);
            var lookahead = audio.AsSpan((k + 1) * 8960, 56);
            mel.Process(chunk, lookahead, output);

            for (var t = 0; t < 56; t += t < 50 ? 11 : 1)    // frames 0, 11, 22, 33, 44 and 55 (the last)
            {
                var expected = ReferenceFrame(y, k * 56 + t, filterbank);
                var diff = MaxDiff(output.AsSpan(t * Mels, Mels).ToArray(), expected);
                Assert.True(diff < 5e-4, $"chunk {k} frame {t}: max |diff| = {diff:E2}");
            }
        }
    }

    [Fact]
    public void Process_CleanModeWithShortLookahead_ShouldZeroTheMissingSamples()
    {
        var audio = LoadSlice("en0001");
        var filterbank = NemotronStreamingMel.CreateMelFilterbank(128, 512, 16000);
        var y = PreEmphasize(audio);
        var output = new float[56 * Mels];

        // 10 of the 40 samples known: the other 30 count as zero in the pre-emphasised domain
        NewMel().Process(audio.AsSpan(0, 8960), audio.AsSpan(8960, 10), output);

        var yPartial = y.Take(8970).ToArray();
        var expected = ReferenceFrame(yPartial, 55, filterbank);
        Assert.True(MaxDiff(output.AsSpan(55 * Mels, Mels).ToArray(), expected) < 5e-4);
    }

    [Fact]
    public void Process_InvalidArguments_ShouldThrow()
    {
        var mel = NewMel();

        Assert.Throws<ArgumentException>(() => mel.Process(ReadOnlySpan<float>.Empty, default, new float[56 * Mels]));
        Assert.Throws<ArgumentException>(() => mel.Process(new float[100], default, new float[Mels]));     // shorter than the 144 samples that make one frame
        Assert.Throws<ArgumentException>(() => mel.Process(new float[8960], default, new float[55 * Mels])); // destination too small
        Assert.Throws<ArgumentOutOfRangeException>(() => new NemotronStreamingMel(128, 500, 160, 400, 16000, 0.97f, 1e-7f));
    }

    // ── an independent reference: naive DFT in double, NeMo's centre-padded STFT ──

    private static double[] PreEmphasize(float[] x)
    {
        var y = new double[x.Length];
        y[0] = x[0];
        for (var i = 1; i < x.Length; i++)
            y[i] = x[i] - (double)0.97f * x[i - 1];
        return y;
    }

    // Global mel frame t is centred on sample 160 * t: window samples [160 t - 200, 160 t + 200), zero outside the signal.
    private static float[] ReferenceFrame(double[] y, int t, float[] filterbank)
    {
        var power = new double[257];
        var centre = 160 * t;
        var windowed = new double[400];
        for (var n = 0; n < 400; n++)
        {
            var i = centre - 200 + n;
            var sample = i >= 0 && i < y.Length ? y[i] : 0.0;
            windowed[n] = sample * (0.5 * (1.0 - Math.Cos(2.0 * Math.PI * n / 399)));
        }
        for (var k = 0; k <= 256; k++)
        {
            double re = 0, im = 0;
            for (var n = 0; n < 400; n++)
            {
                var angle = -2.0 * Math.PI * k * n / 512.0;
                re += windowed[n] * Math.Cos(angle);
                im += windowed[n] * Math.Sin(angle);
            }
            power[k] = re * re + im * im;
        }

        var mel = new float[Mels];
        for (var m = 0; m < Mels; m++)
        {
            double sum = 0;
            for (var k = 0; k <= 256; k++)
                sum += filterbank[m * 257 + k] * power[k];
            mel[m] = (float)Math.Log(sum + 5.96046448e-08);
        }
        return mel;
    }

    private static double MaxDiff(float[] a, float[] b) => a.Zip(b, (x, y) => Math.Abs((double)x - y)).Max();

    // ── data ────────────────────────────────────────────────────────────

    private static float[] LoadSlice(string clip)
    {
        var bytes = File.ReadAllBytes(NemotronTestEnvironment.DataPath("mel", $"{clip}_slice.pcm16"));
        var audio = new float[bytes.Length / 2];
        for (var i = 0; i < audio.Length; i++)
            audio[i] = BitConverter.ToInt16(bytes, 2 * i) / 32768f;
        return audio;
    }

    private static float[] ReadFloats(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }

    private static float[] ReadFloatsGz(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var memory = new MemoryStream();
        gzip.CopyTo(memory);
        var bytes = memory.ToArray();
        var floats = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, floats, 0, bytes.Length);
        return floats;
    }
}
