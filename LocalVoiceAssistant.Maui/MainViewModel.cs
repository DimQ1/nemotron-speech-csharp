using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LocalVoiceAssistant.Maui.Core;
using LocalVoiceAssistant.Maui.Plugins;
using LocalVoiceAssistant.Maui.Services;

namespace LocalVoiceAssistant.Maui;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly VoiceAssistantOrchestrator _orchestrator;
    private readonly IModelBootstrapService _modelBootstrap;
    private readonly string _modelsRoot = Path.Combine(FileSystem.AppDataDirectory, "models");
    private Task? _initializationTask;
    private string _statusText = "Готов";
    private string _draftText = string.Empty;
    private string _modelPath = string.Empty;
    private string _languageCode = "ru";
    private string _executionProvider = "cpu";
    private string _selectedMode = "Chat";
    private string _historyQuery = string.Empty;
    private string _progressText = "Подготовка модели";
    private string _modelStatusText = "Проверяю модель...";
    private string _modelFileText = string.Empty;
    private string _petState = "idle";
    private double _progress;
    private bool _isListening;
    private bool _isBusy;
    private bool _isModelReady;
    private bool _isModelDownloading;
    private bool _isStartupError;
    private bool _disposed;

    public MainViewModel()
    {
        _orchestrator = LvaRuntimeFactory.Create();
        _modelBootstrap = LvaRuntimeFactory.CreateModelBootstrapService();
        _orchestrator.ConversationReceived += OnConversationReceived;
        _orchestrator.RuntimeChanged += OnRuntimeChanged;
        _orchestrator.ProgressChanged += OnProgressChanged;
        _orchestrator.ErrorReceived += OnErrorReceived;

        SendMessageCommand = new Command(async () => await SendMessageAsync());
        ToggleListeningCommand = new Command(async () => await ToggleListeningAsync());
        ClearHistoryCommand = new Command(ClearHistory);
        SearchHistoryCommand = new Command(async () => await SearchHistoryAsync());
        SelectModeCommand = new Command<string>(mode => SelectedMode = mode);

        foreach (var plugin in _orchestrator.Plugins)
            Plugins.Add(plugin);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<ConversationEntry>? MessageAdded;

    public ObservableCollection<ConversationEntry> Messages { get; } = new();
    public ObservableCollection<TaskCard> Tasks { get; } = new();
    public ObservableCollection<IAssistantPlugin> Plugins { get; } = new();
    public ObservableCollection<ConversationEntry> History { get; } = new();
    public ObservableCollection<string> ActionLog { get; } = new();

    public Command SendMessageCommand { get; }
    public Command ToggleListeningCommand { get; }
    public Command ClearHistoryCommand { get; }
    public Command SearchHistoryCommand { get; }
    public Command<string> SelectModeCommand { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string DraftText
    {
        get => _draftText;
        set => SetProperty(ref _draftText, value);
    }

    public string ModelPath
    {
        get => _modelPath;
        set => SetProperty(ref _modelPath, value);
    }

    public string LanguageCode
    {
        get => _languageCode;
        set => SetProperty(ref _languageCode, value);
    }

    public string ExecutionProvider
    {
        get => _executionProvider;
        set => SetProperty(ref _executionProvider, value);
    }

    public string SelectedMode
    {
        get => _selectedMode;
        private set => SetProperty(ref _selectedMode, value);
    }

    public string HistoryQuery
    {
        get => _historyQuery;
        set => SetProperty(ref _historyQuery, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsListening
    {
        get => _isListening;
        private set
        {
            if (!SetProperty(ref _isListening, value))
                return;
            OnPropertyChanged(nameof(ToggleButtonText));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsModelReady
    {
        get => _isModelReady;
        private set
        {
            if (!SetProperty(ref _isModelReady, value))
                return;
            OnPropertyChanged(nameof(CanStartListening));
            OnPropertyChanged(nameof(ModelActionText));
        }
    }

    public bool IsModelDownloading
    {
        get => _isModelDownloading;
        private set
        {
            if (!SetProperty(ref _isModelDownloading, value))
                return;
            OnPropertyChanged(nameof(CanStartListening));
            OnPropertyChanged(nameof(ModelActionText));
        }
    }

    public bool IsStartupError
    {
        get => _isStartupError;
        private set => SetProperty(ref _isStartupError, value);
    }

    public string ModelStatusText
    {
        get => _modelStatusText;
        private set => SetProperty(ref _modelStatusText, value);
    }

    public string ModelFileText
    {
        get => _modelFileText;
        private set => SetProperty(ref _modelFileText, value);
    }

    public string PetState
    {
        get => _petState;
        private set
        {
            if (!SetProperty(ref _petState, value))
                return;
            OnPropertyChanged(nameof(PetStateText));
            OnPropertyChanged(nameof(PetStateColor));
        }
    }

    public string PetStateText => PetState switch
    {
        "listening" => "Слушаю",
        "recognizing" => "Распознаю",
        "thinking" => "Думаю",
        "working" => "Выполняю",
        "success" => "Готово",
        "error" => "Ошибка",
        "cancelled" => "Отменено",
        _ => "Готов"
    };

    public Color PetStateColor => PetState switch
    {
        "listening" => Color.FromArgb("#FFB454"),
        "recognizing" => Color.FromArgb("#7C6CFF"),
        "thinking" or "working" => Color.FromArgb("#FFB454"),
        "success" => Color.FromArgb("#3ECF8E"),
        "error" => Color.FromArgb("#FF6B6B"),
        _ => Color.FromArgb("#7C6CFF")
    };

    public bool CanStartListening => IsModelReady && !IsBusy && !IsModelDownloading;
    public string ModelActionText => IsModelDownloading ? "Загрузка..." : IsModelReady ? "Модель готова" : "Загрузить модель";
    public string ToggleButtonText => IsListening ? "Остановить" : "Слушать";
    public string PlatformText => DeviceInfo.Platform.ToString();
    public string PluginCountText => $"{Plugins.Count} плагинов подключено";

    public Task InitializeAsync()
    {
        if (_initializationTask is null)
            _initializationTask = InitializeCoreAsync();
        return _initializationTask;
    }

    public Task SendMessageFromUiAsync(string text)
    {
        DraftText = text;
        return SendMessageAsync();
    }

    public Task ToggleListeningFromUiAsync() => ToggleListeningAsync();

    public async Task RetryModelDownloadAsync()
    {
        if (IsBusy || IsModelDownloading)
            return;

        _initializationTask = null;
        await InitializeAsync();
    }

    private async Task InitializeCoreAsync()
    {
        IsBusy = true;
        IsModelDownloading = true;
        IsStartupError = false;
        IsModelReady = false;
        PetState = "thinking";
        StatusText = "Подготавливаю локальное распознавание...";
        ModelStatusText = "Проверяю локальную модель...";
        ProgressText = "Подготовка модели";
        Progress = 0;

        try
        {
            var progress = new Progress<ModelDownloadProgress>(OnModelDownloadProgress);
            var result = await _modelBootstrap.EnsureModelAsync(
                _modelsRoot,
                HuggingFaceModelBootstrapService.RecommendedModel,
                progress,
                CancellationToken.None);

            ModelPath = result.ModelPath;
            IsModelReady = result.IsReady;
            ModelStatusText = result.Status;
            ModelFileText = result.ModelPath;
            StatusText = "Готово к распознаванию";
            PetState = "idle";
            ProgressText = "Модель готова";
            Progress = 1;
        }
        catch (OperationCanceledException)
        {
            IsStartupError = true;
            ModelStatusText = "Загрузка модели отменена.";
            StatusText = "Модель не готова";
            PetState = "cancelled";
        }
        catch (Exception ex)
        {
            IsStartupError = true;
            ModelStatusText = $"Не удалось подготовить модель: {ex.Message}";
            StatusText = "Ошибка запуска";
            PetState = "error";
            ProgressText = "Требуется повторить загрузку";
        }
        finally
        {
            IsModelDownloading = false;
            IsBusy = false;
        }
    }

    private void OnModelDownloadProgress(ModelDownloadProgress value)
    {
        ModelStatusText = value.Status;
        ModelFileText = value.CurrentFile;
        ProgressText = value.CurrentFile.Length == 0
            ? value.Status
            : $"{value.Status}: {value.CurrentFile}";
        Progress = value.OverallProgress;
    }

    private async Task SendMessageAsync()
    {
        var text = DraftText.Trim();
        if (text.Length == 0 || IsBusy)
            return;

        DraftText = string.Empty;
        IsBusy = true;
        try
        {
            var response = await _orchestrator.ProcessTextAsync(text, true, CancellationToken.None);
            if (response.ShouldStop)
                IsListening = false;
        }
        catch (Exception ex)
        {
            StatusText = $"Ошибка: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ToggleListeningAsync()
    {
        if (IsBusy)
            return;

        try
        {
            if (!IsModelReady)
            {
                await InitializeAsync();
                if (!IsModelReady)
                    return;
            }

            if (IsListening)
            {
                await _orchestrator.StopAsync(CancellationToken.None);
                IsListening = false;
                StatusText = "Прослушивание остановлено";
                PetState = "cancelled";
                return;
            }

            IsBusy = true;
            PetState = "thinking";
            StatusText = "Загружаю ASR и запускаю микрофон...";
            await _orchestrator.StartAsync(
                ModelPath,
                LanguageCode,
                ExecutionProvider,
                CancellationToken.None);
            IsListening = true;
            PetState = "listening";
            StatusText = "Слушаю";
        }
        catch (Exception ex)
        {
            IsListening = false;
            IsStartupError = true;
            PetState = "error";
            StatusText = $"Ошибка запуска распознавания: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearHistory()
    {
        Messages.Clear();
        History.Clear();
        ActionLog.Clear();
        StatusText = "История текущего экрана очищена";
    }

    private async Task SearchHistoryAsync()
    {
        History.Clear();
        if (string.IsNullOrWhiteSpace(HistoryQuery))
            return;

        var results = await _orchestrator.SearchHistoryAsync(HistoryQuery, CancellationToken.None);
        foreach (var entry in results)
            History.Add(entry);
    }

    private void OnConversationReceived(object? sender, ConversationEntry entry)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Messages.Add(entry);
            MessageAdded?.Invoke(this, entry);
            if (Messages.Count > 50)
                Messages.RemoveAt(0);
            if (entry.Role == "assistant")
            {
                ActionLog.Insert(0, entry.Text);
                if (ActionLog.Count > 10)
                    ActionLog.RemoveAt(ActionLog.Count - 1);
            }
        });
    }

    private void OnRuntimeChanged(object? sender, RuntimeSnapshot snapshot)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusText = snapshot.StatusText;
            if (snapshot.State is RuntimeState.Error or RuntimeState.Stopped)
            {
                IsListening = false;
                PetState = snapshot.State == RuntimeState.Error ? "error" : "cancelled";
            }
            else if (snapshot.State == RuntimeState.Listening)
                PetState = "listening";
            else if (snapshot.State == RuntimeState.Executing)
                PetState = "working";
        });
    }

    private void OnProgressChanged(object? sender, PluginProgress progress)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ProgressText = progress.Message;
            Progress = Math.Clamp(progress.Percent ?? 0, 0, 1);
        });
    }

    private void OnErrorReceived(object? sender, string error)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusText = $"Ошибка: {error}";
            IsListening = false;
        });
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _orchestrator.ConversationReceived -= OnConversationReceived;
        _orchestrator.RuntimeChanged -= OnRuntimeChanged;
        _orchestrator.ProgressChanged -= OnProgressChanged;
        _orchestrator.ErrorReceived -= OnErrorReceived;
        _orchestrator.Dispose();
        _modelBootstrap.Dispose();
    }
}
