using SpeechLib.Audio;

namespace SpeechLib;

/// <summary>Audio source abstraction for live capture.</summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Sample rate of the captured audio (before resampling).</summary>
    int SourceSampleRate { get; }

    /// <summary>
    /// Start capturing. Samples are pushed as <c>float[]</c> batches to <paramref name="buffer"/>
    /// and <paramref name="signal"/> is set whenever new data arrives.
    /// </summary>
    void Start(ConcurrentQueueWrapper buffer, ManualResetEventSlim signal, ref bool isRunning);
}
