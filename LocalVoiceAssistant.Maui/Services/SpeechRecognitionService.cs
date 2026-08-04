#if WINDOWS
using SpeechLib;
using SpeechLib.Audio;
using SpeechLib.Models;

namespace LocalVoiceAssistant.Maui.Services;

public sealed class SpeechRecognitionService : ISpeechRecognitionService
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCts;
    private Task? _listeningTask;
    private IAudioSource? _audioSource;
    private IStreamingSpeechRecognizer? _recognizer;
    private CaptureState? _captureState;
    private ConcurrentQueueWrapper? _queue;
    private ManualResetEventSlim? _dataSignal;
    private TaskCompletionSource<bool>? _startupCompletion;
    private bool _isListening;

    public bool IsSupported => true;
    public string DisplayName => "Windows SpeechLib + Nemotron";

    public event EventHandler<string>? TranscriptReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    public async Task StartAsync(string modelPath, string language, string executionProvider, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> startupCompletion;
        lock (_sync)
        {
            if (_isListening)
                return;

            _activeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startupCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _startupCompletion = startupCompletion;
            _isListening = true;
            StatusChanged?.Invoke(this, "Подготавливаю локальную модель распознавания...");
        }

        var activeToken = _activeCts!.Token;
        _listeningTask = Task.Run(
            () => RunListeningLoop(modelPath, language, executionProvider, activeToken),
            CancellationToken.None);
        await startupCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!_isListening)
                return Task.CompletedTask;

            _activeCts?.Cancel();
            _captureState?.Stop();
            _dataSignal?.Set();
            _startupCompletion?.TrySetCanceled(cancellationToken);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _listeningTask?.GetAwaiter().GetResult();
        _recognizer?.Dispose();
        _audioSource?.Dispose();
        _captureState?.Dispose();
        _dataSignal?.Dispose();
        _activeCts?.Dispose();
    }

    private void RunListeningLoop(string modelPath, string language, string executionProvider, CancellationToken cancellationToken)
    {
        try
        {
            var resolvedPath = string.IsNullOrWhiteSpace(modelPath)
                ? Path.Combine(FileSystem.AppDataDirectory, "models")
                : modelPath;

            if (!Directory.Exists(resolvedPath))
                throw new DirectoryNotFoundException($"Model path not found: {resolvedPath}");

            var langId = LanguageMapper.Resolve(language);
            var recognizer = new ModelSession(resolvedPath, executionProvider, langId, useVad: true);
            var audioSource = Transcriber.CreateAudioSource(CaptureMode.Mic, recognizer.SampleRate);
            var queue = new ConcurrentQueueWrapper();
            using var signal = new ManualResetEventSlim(false);
            using var captureState = new CaptureState();

            lock (_sync)
            {
                _recognizer = recognizer;
                _audioSource = audioSource;
                _captureState = captureState;
                _queue = queue;
                _dataSignal = signal;
            }

            StatusChanged?.Invoke(this, "Listening...");

            var captureThread = new Thread(() =>
            {
                try
                {
                    audioSource.Start(queue, signal, captureState);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, ex.Message);
                }
                finally
                {
                    captureState.Stop();
                    signal.Set();
                }
            })
            {
                IsBackground = true,
                Name = "LVA-Capture"
            };

            captureThread.Start();
            _startupCompletion?.TrySetResult(true);

            while (!cancellationToken.IsCancellationRequested && (captureState.IsRunning || captureThread.IsAlive || !queue.IsEmpty))
            {
                var gotData = false;
                while (queue.TryDequeue(out var batch))
                {
                    gotData = true;
                    var text = ((IStreamingSpeechRecognizer)recognizer).ProcessAudio(batch);
                    if (!string.IsNullOrWhiteSpace(text))
                        TranscriptReceived?.Invoke(this, text);
                }

                if (!gotData)
                {
                    signal.Wait(25);
                    signal.Reset();
                }
            }

            while (queue.TryDequeue(out var batch))
            {
                var text = ((IStreamingSpeechRecognizer)recognizer).ProcessAudio(batch);
                if (!string.IsNullOrWhiteSpace(text))
                    TranscriptReceived?.Invoke(this, text);
            }

            var flushText = ((IStreamingSpeechRecognizer)recognizer).Flush();
            if (!string.IsNullOrWhiteSpace(flushText))
                TranscriptReceived?.Invoke(this, flushText);
        }
        catch (OperationCanceledException)
        {
            _startupCompletion?.TrySetCanceled(cancellationToken);
            StatusChanged?.Invoke(this, "Stopped");
        }
        catch (Exception ex)
        {
            _startupCompletion?.TrySetException(ex);
            ErrorOccurred?.Invoke(this, ex.Message);
            StatusChanged?.Invoke(this, "Stopped");
        }
        finally
        {
            lock (_sync)
            {
                _captureState?.Stop();
                _dataSignal?.Set();
                _audioSource?.Dispose();
                _recognizer?.Dispose();
                _audioSource = null;
                _recognizer = null;
                _captureState = null;
                _queue = null;
                _dataSignal = null;
                _startupCompletion = null;
                _isListening = false;
            }
        }
    }
}
#else
namespace LocalVoiceAssistant.Maui.Services;

public sealed class SpeechRecognitionService : ISpeechRecognitionService
{
    public bool IsSupported => false;
    public string DisplayName => "Platform stub";

    public event EventHandler<string>? TranscriptReceived;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    public Task StartAsync(string modelPath, string language, string executionProvider, CancellationToken cancellationToken)
    {
        StatusChanged?.Invoke(this, "Speech recognition is not available on this platform yet.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
#endif
