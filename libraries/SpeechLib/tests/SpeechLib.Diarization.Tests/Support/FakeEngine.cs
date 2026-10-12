namespace SpeechLib.Diarization.Tests;

/// <summary>
/// Stand-in for the two ONNX graphs. The "front-end" writes the mean of the middle 10 ms of each mel frame's window into
/// column 0; the "step" turns that value into speaker activity: above +0.01 means speaker 0, below -0.01 means speaker 1.
/// (A DC level A after pre-emphasis is 0.03 A, so +-0.5 encodes the two speakers and 0 is silence.) This exercises the
/// real scheduling, buffering, cache and post-processing code without a model.
/// </summary>
internal sealed class FakeEngine : IDiarizationEngine
{
    public int FrontendCalls, StepCalls;
    public int MaxCachedFrames;
    public readonly List<(int MelFrames, int Cached)> Steps = [];
    public Action? OnStep;
    private int _disposed;

    public bool IsDisposed => _disposed != 0;

    public float[] RunFrontend(float[] samples)
    {
        FrontendCalls++;
        int frames = (samples.Length - DiarizationConfig.NFft) / DiarizationConfig.HopSamples + 1;
        var mel = new float[frames * DiarizationConfig.MelBins];
        for (int r = 0; r < frames; r++)
        {
            double sum = 0;
            int c = r * DiarizationConfig.HopSamples + DiarizationConfig.CenterPad;
            for (int i = c - 80; i < c + 80; i++) sum += samples[i];
            mel[r * DiarizationConfig.MelBins] = (float)(sum / 160);
        }
        return mel;
    }

    public StepOutput RunStep(float[] features, int melFrames, float[] cached, int cachedFrames)
    {
        StepCalls++;
        OnStep?.Invoke();
        Steps.Add((melFrames, cachedFrames));
        MaxCachedFrames = Math.Max(MaxCachedFrames, cachedFrames);
        const int N = DiarizationConfig.NumSpeakers;
        int nIn = melFrames / 8;
        var p10 = new float[8 * (cachedFrames + nIn) * N];
        for (int j = 0; j < melFrames; j++)
        {
            float v = features[j * DiarizationConfig.MelBins];
            int row = 8 * cachedFrames + j;
            if (v > 0.01f) p10[row * N] = 0.9f;
            else if (v < -0.01f) p10[row * N + 1] = 0.9f;
        }
        var p80 = new float[(cachedFrames + nIn) * N];
        for (int e = 0; e < cachedFrames + nIn; e++)
            for (int k = 0; k < N; k++)
            {
                float s = 0;
                for (int j = 0; j < 8; j++) s += p10[(e * 8 + j) * N + k];
                p80[e * N + k] = s / 8;
            }
        // distinct embeddings so that the cache logic moves real data around
        var emb = new float[nIn * DiarizationConfig.HiddenSize];
        for (int i = 0; i < emb.Length; i++) emb[i] = i % 7;
        return new StepOutput(emb, p10, p80);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    public static DiarizationModel CreateModel(FakeEngine engine, DiarizationConfig? config = null) =>
        new(new DiarizationModelDescriptor("fake", DiarizationPrecision.Int8, "s", "f", "c", 0, [DiarizationPrecision.Int8]),
            config ?? TestConfig.Default, engine, "Cpu");

    /// <summary>Renders segments of constant DC level into 16 kHz audio: +0.5 speaker 0, -0.5 speaker 1, 0 silence.</summary>
    public static float[] Audio(double seconds, params (double Start, double End, float Level)[] parts)
    {
        var a = new float[(int)(seconds * 16000)];
        foreach (var (s, e, l) in parts)
            for (int i = (int)(s * 16000); i < Math.Min(a.Length, (int)(e * 16000)); i++) a[i] = l;
        return a;
    }
}

internal static class TestConfig
{
    private const string Modes =
        "\"modes\": {" +
        "\"low_latency\": {\"chunk_len\": 9, \"right_context\": 4, \"fifo_len\": 264, \"update_period\": 222}," +
        "\"very_low_latency\": {\"chunk_len\": 6, \"right_context\": 2, \"fifo_len\": 264, \"update_period\": 222}," +
        "\"ultra_low_latency\": {\"chunk_len\": 3, \"right_context\": 1, \"fifo_len\": 264, \"update_period\": 222}," +
        "\"ncpp_v3_streaming\": {\"chunk_len\": 13, \"right_context\": 1, \"fifo_len\": 80, \"update_period\": 40}," +
        "\"eco_2s\": {\"chunk_len\": 24, \"right_context\": 2, \"fifo_len\": 48, \"update_period\": 48}},";

    public static string Json(int silenceLen = 512, int numSpeakers = 8, bool includeModes = true)
    {
        string modes = includeModes ? Modes : "\"modes\": {},";
        string silence = string.Join(",", Enumerable.Repeat("0.001", silenceLen));
        return "{\"source\": \"test\", \"sample_rate\": 16000, " +
               "\"frontend\": {\"preemphasis\": 0.97, \"n_fft\": 512, \"win_length\": 400, \"hop_length\": 160, \"center_pad\": 256, \"n_mels\": 128}, " +
               "\"subsampling_factor\": 8, \"num_speakers\": " + numSpeakers + ", \"hidden_size\": 512, " +
               "\"aosc\": {\"spkcache_len\": 264, \"sil_frames_per_spk\": 1, \"pred_score_threshold\": 0.25, \"latest_frames_score_boost\": 0.05, " +
               "\"strong_boost_rate\": 0.75, \"weak_boost_rate\": 1.5, \"min_pos_scores_rate\": 0.5}, " +
               modes +
               "\"offline\": {\"chunk_len\": 340, \"right_context\": 40, \"fifo_len\": 40, \"update_period\": 300}, " +
               "\"silence_embed\": [" + silence + "]}";
    }

    public static DiarizationConfig Default { get; } = DiarizationConfig.Parse(Json());
}
