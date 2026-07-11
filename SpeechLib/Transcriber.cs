using SpeechLib.Audio;
using SpeechLib.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace SpeechLib;

/// <summary>
/// Orchestrates transcription for file mode and live capture mode.
/// Works with any <see cref="IStreamingSpeechRecognizer"/> implementation.
/// </summary>
public static class Transcriber
{
    /// <summary>Regex to match language tags like &lt;en-US&gt;, &lt;bg-BG&gt;, &lt;de-DE&gt;.</summary>
    private static readonly Regex LanguageTagPattern = new(
        @"<\w{2,3}(-\w{2,4})?>", RegexOptions.Compiled);

    /// <summary>Transcribe a pre-recorded audio file.</summary>
    public static string RunFile(string audioPath, IStreamingSpeechRecognizer recognizer)
        => RunFile(audioPath, recognizer, wordTimestamps: false, out _);

    /// <summary>
    /// Transcribe a pre-recorded audio file with optional word-level timestamps.
    /// </summary>
    /// <param name="audioPath">Path to the audio file.</param>
    /// <param name="recognizer">The speech recognizer.</param>
    /// <param name="wordTimestamps">When true, computes and outputs word-level start/end times.</param>
    /// <param name="timings">Receives the list of word timings (empty when <paramref name="wordTimestamps"/> is false).</param>
    /// <returns>The full transcript text.</returns>
    public static string RunFile(string audioPath, IStreamingSpeechRecognizer recognizer,
        bool wordTimestamps, out List<WordTiming> timings)
    {
        var audio = AudioUtils.LoadFile(audioPath, recognizer.SampleRate);
        var totalDuration = audio.Length / (double)recognizer.SampleRate;
        Console.WriteLine($"Audio: {totalDuration:F1}s ({audio.Length} samples)");
        Console.WriteLine(new string('-', 60));

        var transcript = new StringBuilder();
        timings = new List<WordTiming>();
        var sampleRate = (double)recognizer.SampleRate;

        for (int i = 0; i < audio.Length; i += recognizer.ChunkSamples)
        {
            int remaining = Math.Min(recognizer.ChunkSamples, audio.Length - i);
            var chunk = audio[i..(i + remaining)];

            // Capture token count BEFORE ProcessAudio (it's set by the previous call's DecodeTokens).
            // We read it after ProcessAudio returns, when LastTokenCount reflects the current chunk.
            var text = recognizer.ProcessAudio(chunk);

            if (text is not null)
            {
                transcript.Append(text);

                if (wordTimestamps)
                {
                    double chunkStart = i / sampleRate;
                    double chunkEnd = (i + remaining) / sampleRate;
                    AddWordTimings(timings, text, chunkStart, chunkEnd, recognizer.LastTokenCount);
                }
            }
        }

        var flush = recognizer.Flush();
        if (flush is not null)
        {
            transcript.Append(flush);

            if (wordTimestamps)
            {
                double flushStart = (audio.Length - (audio.Length % recognizer.ChunkSamples)) / sampleRate;
                if (timings.Count > 0)
                    flushStart = timings[^1].EndSeconds;
                int tokenCount = recognizer.LastTokenCount;
                AddWordTimings(timings, flush, flushStart, totalDuration, tokenCount);
            }
        }

        Console.WriteLine($"\n{new string('=', 60)}");
        Console.WriteLine($"  {transcript.ToString().Trim()}");
        Console.WriteLine(new string('=', 60));

        if (wordTimestamps && timings.Count > 0)
        {
            Console.WriteLine($"\n  Word timings ({timings.Count} words):");
            Console.WriteLine(new string('-', 60));
            foreach (var wt in timings)
            {
                Console.WriteLine($"  [{wt.StartSeconds:F2}s -> {wt.EndSeconds:F2}s] {wt.Word}");
            }
            Console.WriteLine(new string('-', 60));
        }

        return transcript.ToString();
    }

    private static void AddWordTimings(List<WordTiming> timings, string text,
        double chunkStartSec, double chunkEndSec, int tokenCount = 0)
    {
        // Strip language tags — they are metadata, not spoken words.
        text = LanguageTagPattern.Replace(text, "").Trim();
        if (text.Length == 0) return;

        // Split into tokens.
        var rawTokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (rawTokens.Length == 0) return;

        // Classify tokens: words vs punctuation-only.
        // Punctuation tokens are merged into the preceding word's time span.
        var words = new List<string>();
        foreach (var token in rawTokens)
        {
            if (IsPunctuationOnly(token))
            {
                if (words.Count > 0)
                    words[^1] += token;
            }
            else
            {
                words.Add(token);
            }
        }

        if (words.Count == 0) return;

        double duration = chunkEndSec - chunkStartSec;

        // Phase 2: use token count for token-aware time distribution.
        // Each non-blank token ≈ one encoder frame (~80ms) of speech.
        // Estimate tokens per word from the chunk's avg chars-per-token ratio,
        // then distribute chunk time proportionally to estimated token counts.
        double totalWeight;
        double[] weights;

        if (tokenCount > 0)
        {
            double totalChars = words.Sum(w => (double)w.Length);
            double avgCharsPerToken = totalChars / tokenCount;

            weights = new double[words.Count];
            for (int i = 0; i < words.Count; i++)
            {
                // Estimate token count per word, minimum 1 token per word.
                weights[i] = Math.Max(1.0, words[i].Length / avgCharsPerToken);
            }
            totalWeight = weights.Sum();
        }
        else
        {
            // Fallback: weight by character length.
            weights = words.Select(w => (double)w.Length).ToArray();
            totalWeight = weights.Sum();
        }

        double offset = chunkStartSec;
        for (int i = 0; i < words.Count; i++)
        {
            double wordDuration = duration * (weights[i] / totalWeight);
            timings.Add(new WordTiming
            {
                Word = words[i],
                StartSeconds = offset,
                EndSeconds = offset + wordDuration
            });
            offset += wordDuration;
        }
    }

    private static bool IsPunctuationOnly(string token)
    {
        foreach (char c in token)
        {
            if (!char.IsPunctuation(c) && c != '\'')
                return false;
        }
        return true;
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
                dataSignal.Wait(10);
                dataSignal.Reset();
            }

            if (!captureState.IsRunning && buffer.IsEmpty &&
                (DateTime.UtcNow - lastAudio).TotalSeconds > 1.5)
                break;
        }

        source.Dispose();
        // Wait for capture thread to finish (non-blocking poll, max ~1s)
        for (int i = 0; i < 100 && captureThread.IsAlive; i++)
            Thread.Sleep(10);

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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Transcriber] Warmup: {ex.Message}");
        }
    }

    /// <summary>Create an <see cref="IAudioSource"/> for the given capture mode.</summary>
    public static IAudioSource CreateAudioSource(CaptureMode mode, int sampleRate) => mode switch
    {
        CaptureMode.Mic or CaptureMode.Loopback or CaptureMode.Mix => new BufferedCaptureSource(mode, sampleRate),
        _ => throw new InvalidOperationException($"Capture mode '{mode}' is not a live source.")
    };
}
