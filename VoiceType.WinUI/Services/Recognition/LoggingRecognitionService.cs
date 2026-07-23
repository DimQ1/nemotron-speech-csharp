using System.Diagnostics;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services.Recognition;

/// <summary>Decorator that adds structured logging around IRecognitionService operations.</summary>
public sealed class LoggingRecognitionService : IRecognitionService
{
    private readonly IRecognitionService _inner;

    public LoggingRecognitionService(IRecognitionService inner) => _inner = inner;

    public bool IsRunning => _inner.IsRunning;
    public bool IsMuted => _inner.IsMuted;
    public int SampleRate => _inner.SampleRate;
    public string AccumulatedText => _inner.AccumulatedText;

    public event Action<string>? PartialResult
    {
        add => _inner.PartialResult += value;
        remove => _inner.PartialResult -= value;
    }

    public event Action<string>? FinalResult
    {
        add => _inner.FinalResult += value;
        remove => _inner.FinalResult -= value;
    }

    public event Action? Stopped
    {
        add => _inner.Stopped += value;
        remove => _inner.Stopped -= value;
    }

    public void Initialize(AppSettings settings)
    {
        Console.WriteLine($"[VoiceType] Initializing recognizer: path={settings.ModelPath}, ep={settings.ExecutionProvider}, lang={settings.Language}, vad={settings.UseVad}");
        var sw = Stopwatch.StartNew();
        _inner.Initialize(settings);
        Console.WriteLine($"[VoiceType] Recognizer initialized in {sw.ElapsedMilliseconds}ms");
    }

    public void Start(AppSettings settings)
    {
        Console.WriteLine("[VoiceType] Starting recognition...");
        _inner.Start(settings);
        Console.WriteLine("[VoiceType] Recognition started OK");
    }

    public void Stop()
    {
        Console.WriteLine("[VoiceType] Stopping recognition...");
        _inner.Stop();
    }

    public void SetMuted(bool muted)
    {
        _inner.SetMuted(muted);
        Console.WriteLine($"[VoiceType] Capture {(muted ? "muted" : "unmuted")}");
    }

    public string? SaveAudio(string fileNameBase) => _inner.SaveAudio(fileNameBase);
    public void Dispose() => _inner.Dispose();
}