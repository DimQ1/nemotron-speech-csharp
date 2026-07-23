namespace VoiceType.WinUI.Interfaces;

using VoiceType.WinUI.Models;

/// <summary>Core recognition service abstraction.</summary>
public interface IRecognitionService : IDisposable
{
    bool IsRunning { get; }
    bool IsMuted { get; }
    int SampleRate { get; }
    string AccumulatedText { get; }

    event Action<string>? PartialResult;
    event Action<string>? FinalResult;
    event Action? Stopped;

    void Initialize(AppSettings settings);
    void Start(AppSettings settings);
    void Stop();
    void SetMuted(bool muted);
    string? SaveAudio(string fileNameBase);
}
