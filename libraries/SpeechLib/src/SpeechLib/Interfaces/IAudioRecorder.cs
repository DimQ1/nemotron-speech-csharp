namespace SpeechLib;

/// <summary>
/// Records a stream of float audio samples to a compressed/PCM file in the background.
/// Append is cheap (buffered); encoding runs on a background thread.
/// </summary>
public interface IAudioRecorder : IDisposable
{
    /// <summary>Starts a new recording session into a temporary file.</summary>
    /// <param name="tempDirectory">
    /// Directory for the temporary in-progress file. Created if missing.
    /// </param>
    void Start(string tempDirectory);

    /// <summary>Buffers samples for encoding. No-op when not recording.</summary>
    Task AppendAsync(float[] samples);

    /// <summary>
    /// Stops recording, finalizes encoding and moves the result next to
    /// <paramref name="filePath"/> (extension is replaced by the recorder's format).
    /// Returns the saved file path, or <c>null</c> when nothing was recorded or encoding failed.
    /// </summary>
    string? StopAndSave(string filePath);

    /// <summary>File extension produced by this recorder (e.g. ".mp3", ".wav").</summary>
    string FileExtension { get; }
}

/// <summary>Creates <see cref="IAudioRecorder"/> instances for a concrete audio provider.</summary>
public interface IAudioRecorderFactory
{
    /// <summary>Creates a recorder writing at <paramref name="sampleRate"/> Hz, 16-bit mono.</summary>
    IAudioRecorder Create(int sampleRate);
}
