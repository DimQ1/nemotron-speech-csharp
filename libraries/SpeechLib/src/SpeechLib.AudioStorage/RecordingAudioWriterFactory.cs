namespace SpeechLib.AudioStorage;

/// <summary>Creates the writer for a storage format.</summary>
public static class RecordingAudioWriterFactory
{
    /// <summary>
    /// Opens a writer at <paramref name="pathWithoutExtension"/> + the format's extension
    /// (<c>.ogg</c> / <c>.wav</c>); audio goes to that path + <c>.part</c> until completed.
    /// </summary>
    /// <param name="queued">
    /// True (default) wraps the writer in <see cref="QueuedRecordingAudioWriter"/> so
    /// <see cref="IRecordingAudioWriter.Append"/> never blocks on encoding or disk I/O.
    /// </param>
    public static IRecordingAudioWriter Create(
        string pathWithoutExtension,
        RecordingAudioFormat format,
        int sampleRate = RecordingAudioReader.RecognizerSampleRate,
        bool queued = true)
    {
        ArgumentNullException.ThrowIfNull(format);
        var path = pathWithoutExtension + format.FileExtension;
        IRecordingAudioWriter writer = format.Codec switch
        {
            RecordingAudioCodec.OggOpus => new OggOpusRecordingWriter(path, format, sampleRate),
            RecordingAudioCodec.Wav => new WavRecordingWriter(path, sampleRate),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        return queued ? new QueuedRecordingAudioWriter(writer) : writer;
    }
}
