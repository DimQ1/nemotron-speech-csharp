namespace LocalVoiceAssistant.Maui.Services;

public interface ISpeechRecognitionService : IDisposable
{
    bool IsSupported { get; }
    string DisplayName { get; }

    event EventHandler<string>? TranscriptReceived;
    event EventHandler<string>? StatusChanged;
    event EventHandler<string>? ErrorOccurred;

    Task StartAsync(string modelPath, string language, string executionProvider, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}
