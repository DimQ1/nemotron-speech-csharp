using System.Text;

namespace SpeechLib;

/// <summary>Runs provider-neutral streaming transcription over a live audio source.</summary>
public static class LiveTranscriber
{
    /// <summary>
    /// Captures audio until the source stops, drains all queued batches, and flushes the recognizer.
    /// The source is disposed before this method returns.
    /// </summary>
    /// <param name="onTextDelta">
    /// Optional callback invoked with each new transcription delta as it is decoded
    /// (including the final flush). Lets callers stream translation in parallel.
    /// </param>
    public static string Run(
        IAudioSource source,
        string label,
        IStreamingSpeechRecognizer recognizer,
        Action<string>? onTextDelta = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(recognizer);

        Console.WriteLine($"  Capture: {label}");
        Console.WriteLine($"  Sample rate: {recognizer.SampleRate} Hz, Chunk: {recognizer.ChunkSamples} samples " +
                          $"({recognizer.ChunkSamples * 1000.0 / recognizer.SampleRate:F0} ms)");
        Console.WriteLine("  Press Ctrl+C to stop. Speaking...");
        Console.WriteLine(new string('-', 60));

        var buffer = new Audio.ConcurrentQueueWrapper();
        using var dataSignal = new ManualResetEventSlim(false);
        using var captureState = new CaptureState();
        var transcript = new StringBuilder();

        Warmup(recognizer);

        Exception? captureError = null;
        var captureThread = new Thread(() =>
        {
            try
            {
                source.Start(buffer, dataSignal, captureState);
            }
            catch (Exception ex)
            {
                captureError = ex;
            }
            finally
            {
                captureState.Stop();
                dataSignal.Set();
            }
        })
        {
            IsBackground = true,
            Name = "SpeechLib-capture"
        };

        captureThread.Start();
        Console.WriteLine("  [Listening...]");

        try
        {
            while (captureState.IsRunning || captureThread.IsAlive || !buffer.IsEmpty)
            {
                var gotData = false;
                while (buffer.TryDequeue(out var batch))
                {
                    AppendResult(transcript, recognizer.ProcessAudio(batch), onTextDelta);
                    gotData = true;
                }

                if (!gotData)
                {
                    dataSignal.Wait(10);
                    dataSignal.Reset();
                }
            }

            captureState.Stop();
            captureThread.Join();

            while (buffer.TryDequeue(out var finalBatch))
                AppendResult(transcript, recognizer.ProcessAudio(finalBatch), onTextDelta);

            if (captureError is not null)
                throw new InvalidOperationException("Audio capture failed.", captureError);

            AppendResult(transcript, recognizer.Flush(), onTextDelta);
        }
        finally
        {
            captureState.Stop();
            captureThread.Join();
            source.Dispose();
        }

        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine($"  {transcript.ToString().Trim()}");
        Console.WriteLine(new string('=', 60));

        return transcript.ToString();
    }

    private static void Warmup(IStreamingSpeechRecognizer recognizer)
    {
        try
        {
            recognizer.ProcessAudio(new float[recognizer.ChunkSamples]);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Transcriber] Warmup: {ex.Message}");
        }
    }

    private static void AppendResult(StringBuilder transcript, string? text, Action<string>? onTextDelta)
    {
        if (text is null)
            return;

        transcript.Append(text);
        onTextDelta?.Invoke(text);
    }
}