using SpeechLib.Audio;
using SpeechLib.Models;
using System.Text;

namespace SpeechLib;

/// <summary>
/// Orchestrates transcription for file mode and live capture mode.
/// Works with any <see cref="IStreamingSpeechRecognizer"/> implementation.
/// </summary>
public static class Transcriber
{
    /// <summary>Transcribe a pre-recorded audio file.</summary>
    public static string RunFile(string audioPath, IStreamingSpeechRecognizer recognizer)
    {
        var audio = AudioUtils.LoadFile(audioPath, recognizer.SampleRate);
        Console.WriteLine($"Audio: {audio.Length / (double)recognizer.SampleRate:F1}s ({audio.Length} samples)");
        Console.WriteLine(new string('-', 60));

        var transcript = new StringBuilder();
        for (int i = 0; i < audio.Length; i += recognizer.ChunkSamples)
        {
            int remaining = Math.Min(recognizer.ChunkSamples, audio.Length - i);
            var chunk = audio[i..(i + remaining)];
            var text = recognizer.ProcessAudio(chunk);
            if (text is not null)
                transcript.Append(text);
        }

        var flush = recognizer.Flush();
        if (flush is not null)
            transcript.Append(flush);

        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine($"  {transcript.ToString().Trim()}");
        Console.WriteLine(new string('=', 60));

        return transcript.ToString();
    }

    /// <summary>
    /// Transcribe from a live audio source.
    /// Feeds audio directly to the processor as it arrives.
    /// The recognizer buffers internally and returns results when ready.
    /// </summary>
    public static string RunLive(IAudioSource source, string label, IStreamingSpeechRecognizer recognizer)
    {
        Console.WriteLine($"  Capture: {label}");
        Console.WriteLine($"  Sample rate: {recognizer.SampleRate} Hz, Chunk: {recognizer.ChunkSamples} samples " +
                          $"({recognizer.ChunkSamples * 1000.0 / recognizer.SampleRate:F0} ms)");
        Console.WriteLine("  Press Ctrl+C to stop. Speaking...");
        Console.WriteLine(new string('-', 60));

        var buffer = new ConcurrentQueueWrapper();
        var dataSignal = new ManualResetEventSlim(false);
        var captureState = new CaptureState();
        var transcript = new StringBuilder();

        Warmup(recognizer);

        var captureThread = new Thread(() => source.Start(buffer, dataSignal, captureState))
            { IsBackground = true };
        captureThread.Start();

        Console.WriteLine("  [Listening...]");
        var lastAudio = DateTime.UtcNow;

        while (captureState.IsRunning || !buffer.IsEmpty)
        {
            bool gotData = false;
            while (buffer.TryDequeue(out var batch))
            {
                var text = recognizer.ProcessAudio(batch);
                if (text is not null)
                    transcript.Append(text);
                gotData = true;
            }

            if (gotData)
                lastAudio = DateTime.UtcNow;
            else
            {
                dataSignal.Wait(50);
                dataSignal.Reset();
            }

            if (!captureState.IsRunning && buffer.IsEmpty &&
                (DateTime.UtcNow - lastAudio).TotalSeconds > 1.5)
                break;
        }

        source.Dispose();
        captureThread.Join(2000);

        // Flush remaining
        var flush = recognizer.Flush();
        if (flush is not null)
            transcript.Append(flush);

        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine($"  {transcript.ToString().Trim()}");
        Console.WriteLine(new string('=', 60));

        return transcript.ToString();
    }

    private static void Warmup(IStreamingSpeechRecognizer recognizer)
    {
        try
        {
            var silent = new float[recognizer.ChunkSamples];
            recognizer.ProcessAudio(silent);
        }
        catch { /* best-effort */ }
    }

    /// <summary>Create an <see cref="IAudioSource"/> for the given capture mode.</summary>
    public static IAudioSource CreateAudioSource(CaptureMode mode, int sampleRate) => mode switch
    {
        CaptureMode.Mic => new MicAudioSource(),
        CaptureMode.Loopback => new LoopbackAudioSource(sampleRate),
        CaptureMode.Mix => new MixAudioSource(sampleRate),
        _ => throw new InvalidOperationException($"Capture mode '{mode}' is not a live source.")
    };
}
