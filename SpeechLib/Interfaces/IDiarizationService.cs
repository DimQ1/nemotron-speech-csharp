using SpeechLib.Models;

namespace SpeechLib;

/// <summary>
/// Speaker diarization service — identifies who spoke when.
/// Implementations process audio and return time segments with speaker labels.
/// </summary>
public interface IDiarizationService : IDisposable
{
    /// <summary>Sample rate the diarization model expects (typically 16000 Hz).</summary>
    int SampleRate { get; }

    /// <summary>
    /// Run diarization on full audio and return speaker segments.
    /// </summary>
    /// <param name="audio">16kHz mono float32 audio samples.</param>
    /// <returns>Time-ordered speaker segments with labels.</returns>
    List<DiarizationSegment> Diarize(float[] audio);
}
