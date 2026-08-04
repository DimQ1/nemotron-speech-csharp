namespace LVA.Nlu.Vad;

/// <summary>Streaming voice-activity detector (TZ §4.1).</summary>
public interface IVadService : IDisposable
{
    /// <summary>Raised when speech starts (probability crosses the threshold).</summary>
    event Action? SpeechStarted;

    /// <summary>Raised for every processed frame with the speech probability.</summary>
    event Action<float>? SpeechFrame;

    /// <summary>Raised when speech ends after MinSilenceMs of silence.</summary>
    event Action<TimeSpan>? SpeechEnded;

    /// <summary>Feed a 30 ms PCM16 mono 16 kHz frame (480 samples as float).</summary>
    void AcceptFrame(ReadOnlySpan<float> samples);

    /// <summary>Reset the streaming state (new dialog).</summary>
    void Reset();

    bool IsSpeechActive { get; }
}
