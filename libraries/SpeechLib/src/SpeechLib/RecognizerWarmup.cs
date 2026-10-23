namespace SpeechLib;

/// <summary>Primes a recognizer before a live session without leaving a trace in its stream.</summary>
public static class RecognizerWarmup
{
    /// <summary>
    /// Feeds one silent chunk so that the first real chunk does not pay for lazy initialization, then resets the
    /// stream: a recognizer that keeps a stream clock (<see cref="ITimedUtteranceRecognizer"/>) would otherwise start the
    /// session one chunk late. A failure is reported on the error stream and never stops the session.
    /// </summary>
    public static void Run(IStreamingSpeechRecognizer recognizer)
    {
        ArgumentNullException.ThrowIfNull(recognizer);
        try
        {
            recognizer.ProcessAudio(new float[recognizer.ChunkSamples]);
            recognizer.ResetStreamingState();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Transcriber] Warmup: {ex.Message}");
        }
    }
}
