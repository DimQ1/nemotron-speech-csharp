using NAudio.Wave.SampleProviders;

namespace NemotronSpeech;

/// <summary>Orchestrates transcription: file mode and live capture mode.</summary>
public static class Transcriber
{
    /// <summary>Transcribe a pre-recorded audio file.</summary>
    public static void RunFile(string audioPath, ModelSession session)
    {
        var audio = AudioUtils.LoadFile(audioPath, session.SampleRate);
        Console.WriteLine($"Audio: {audio.Length / (double)session.SampleRate:F1}s ({audio.Length} samples)");
        Console.WriteLine(new string('-', 60));

        var transcript = "";
        for (int i = 0; i < audio.Length; i += session.ChunkSamples)
        {
            int remaining = Math.Min(session.ChunkSamples, audio.Length - i);
            var chunk = audio[i..(i + remaining)];
            using var inputs = session.ProcessAudio(chunk);
            if (inputs is not null) { session.SetInputs(inputs); transcript += session.DecodeTokens(); }
        }

        using var flush = session.Flush();
        if (flush is not null) { session.SetInputs(flush); transcript += session.DecodeTokens(); }

        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine($"  {transcript.Trim()}");
        Console.WriteLine(new string('=', 60));
    }

    /// <summary>
    /// Transcribe from a live audio source.
    /// Feeds audio directly to the processor as it arrives — no batching or progressive sizing.
    /// The StreamingProcessor buffers internally and returns results when ready.
    /// </summary>
    public static void RunLive(IAudioSource source, string label, ModelSession session)
    {
        Console.WriteLine($"  Capture: {label}");
        Console.WriteLine($"  Sample rate: {session.SampleRate} Hz, Chunk: {session.ChunkSamples} samples " +
                          $"({session.ChunkSamples * 1000.0 / session.SampleRate:F0} ms)");
        Console.WriteLine("  Press Ctrl+C to stop. Speaking...");
        Console.WriteLine(new string('-', 60));

        var buffer = new ConcurrentQueueWrapper();
        var dataSignal = new ManualResetEventSlim(false);
        bool isRunning = true;
        var transcript = "";

        Warmup(session);

        var captureThread = new Thread(() => source.Start(buffer, dataSignal, ref isRunning))
            { IsBackground = true };
        captureThread.Start();

        Console.WriteLine("  [Listening...]");
        var lastAudio = DateTime.UtcNow;

        while (isRunning || !buffer.IsEmpty)
        {
            bool gotData = false;
            while (buffer.TryDequeue(out var batch))
            {
                // Feed directly — processor accumulates internally
                using var inputs = session.ProcessAudio(batch);
                if (inputs is not null)
                {
                    session.SetInputs(inputs);
                    transcript += session.DecodeTokens();
                }
                gotData = true;
            }

            if (gotData)
                lastAudio = DateTime.UtcNow;
            else
                Thread.Sleep(1);

            if (!isRunning && buffer.IsEmpty &&
                (DateTime.UtcNow - lastAudio).TotalSeconds > 1.5)
                break;
        }

        source.Dispose();
        captureThread.Join(2000);

        // Flush remaining
        using var flush = session.Flush();
        if (flush is not null) { session.SetInputs(flush); transcript += session.DecodeTokens(); }

        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine($"  {transcript.Trim()}");
        Console.WriteLine(new string('=', 60));
    }

    private static void Warmup(ModelSession session)
    {
        try
        {
            var silent = new float[session.ChunkSamples];
            using var inputs = session.ProcessAudio(silent);
            if (inputs is not null) { session.SetInputs(inputs); session.DecodeTokens(); }
        }
        catch { /* best-effort */ }
    }
}
