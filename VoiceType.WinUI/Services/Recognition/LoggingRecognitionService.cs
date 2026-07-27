using System.Diagnostics;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;

namespace VoiceType.WinUI.Services.Recognition;

/// <summary>
/// Decorator that adds structured logging around IRecognitionService operations
/// via ISystemTelemetry (ILogger + file + debug output).
/// </summary>
public sealed class LoggingRecognitionService : IRecognitionService
{
    private readonly IRecognitionService _inner;
    private readonly ISystemTelemetry _telemetry;

    public LoggingRecognitionService(IRecognitionService inner, ISystemTelemetry telemetry)
    {
        _inner = inner;
        _telemetry = telemetry;
    }

    public bool IsRunning => _inner.IsRunning;
    public bool IsMuted => _inner.IsMuted;
    public int SampleRate => _inner.SampleRate;
    public string AccumulatedText => _inner.AccumulatedText;

    public ModelState ModelState => _inner.ModelState;

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

    public event Action<ModelState>? ModelStateChanged
    {
        add => _inner.ModelStateChanged += value;
        remove => _inner.ModelStateChanged -= value;
    }

    public async Task LoadModelAsync(AppSettings settings)
    {
        _telemetry.LogInfo("Recognition",
            $"Loading model: path={settings.ModelPath}, ep={settings.ExecutionProvider}, lang={settings.Language}, vad={settings.UseVad}");
        var sw = Stopwatch.StartNew();
        await _inner.LoadModelAsync(settings);
        _telemetry.LogInfo("Recognition", $"Model loaded in {sw.ElapsedMilliseconds}ms (state={_inner.ModelState})");
    }

    public void UnloadModel()
    {
        _telemetry.LogInfo("Recognition", "Unloading model...");
        _inner.UnloadModel();
        _telemetry.LogInfo("Recognition", "Model unloaded");
    }

    public void Start(AppSettings settings)
    {
        _telemetry.LogInfo("Recognition", "Starting recognition...");
        _inner.Start(settings);
        _telemetry.LogInfo("Recognition", "Recognition started OK");
    }

    public void Stop()
    {
        _telemetry.LogInfo("Recognition", "Stopping recognition...");
        _inner.Stop();
    }

    public void SetMuted(bool muted)
    {
        _inner.SetMuted(muted);
        _telemetry.LogInfo("Recognition", $"Capture {(muted ? "muted" : "unmuted")}");
    }

    public string? SaveAudio(string fileNameBase) => _inner.SaveAudio(fileNameBase);
    public void Dispose() => _inner.Dispose();

    public void SetLanguage(string language)
    {
        _telemetry.LogInfo("Recognition", $"Language changed to: {language}");
        _inner.SetLanguage(language);
    }
}