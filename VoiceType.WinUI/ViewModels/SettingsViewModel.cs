using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _original;

    public nint OwnerWindowHandle { get; set; }

    // ---- Observable properties ----

    [ObservableProperty]
    private string _modelsRootPath = "";

    [ObservableProperty]
    private string _selectedModel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelPath))]
    private string _executionProvider = "cpu";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelPath))]
    private string _language = "auto";

    [ObservableProperty]
    private bool _useVad = true;

    [ObservableProperty]
    private int _numBeams = 1;

    [ObservableProperty]
    private double _repetitionPenalty = 1.1;

    [ObservableProperty]
    private string _audioSource = "Mic";

    [ObservableProperty]
    private InjectionMethod _textInjectionMethod = InjectionMethod.InputSimulator;

    [ObservableProperty]
    private bool _stopOnAnyInput = true;

    [ObservableProperty]
    private bool _disableInjectionOnFocusChange = true;

    [ObservableProperty]
    private bool _autoStartRecognition;

    [ObservableProperty]
    private bool _alwaysOnTop = true;

    [ObservableProperty]
    private bool _saveSessions = true;

    [ObservableProperty]
    private string _sessionsPath = AppPaths.SessionsDir;

    [ObservableProperty]
    private bool _saveAudioMp3;

    [ObservableProperty]
    private string _toggleHotkey = "Ctrl+Shift+V";

    [ObservableProperty]
    private string _muteHotkey = "Ctrl+Shift+M";

    [ObservableProperty]
    private string _injectTextHotkey = "Ctrl+Shift+I";

    [ObservableProperty]
    private bool _postProcessingEnabled = true;

    public string ModelPath => (!string.IsNullOrEmpty(ModelsRootPath) && !string.IsNullOrEmpty(SelectedModel))
        ? Path.Combine(ModelsRootPath, SelectedModel)
        : "";

    public ObservableCollection<string> AvailableModels { get; } = new();
    public ObservableCollection<PostProcessingRule> Rules { get; } = new();

    public bool WasSaved { get; private set; }
    public event Action? RequestClose;

    // ---- Constructor ----

    public SettingsViewModel(ISettingsService settingsService, AppSettings settings)
    {
        _settingsService = settingsService;
        _original = settings;

        ModelsRootPath = settings.ModelsRootPath;
        SelectedModel = settings.SelectedModel;
        ExecutionProvider = settings.ExecutionProvider;
        Language = settings.Language;
        UseVad = settings.UseVad;
        NumBeams = settings.NumBeams;
        RepetitionPenalty = settings.RepetitionPenalty;

        if (string.IsNullOrEmpty(ModelsRootPath))
            ModelsRootPath = AppPaths.ModelsDir;

        AudioSource = settings.AudioSource;
        TextInjectionMethod = settings.TextInjectionMethod;
        StopOnAnyInput = settings.StopOnAnyInput;
        SaveSessions = settings.SaveSessions;
        SessionsPath = settings.SessionsPath;
        SaveAudioMp3 = settings.SaveAudioMp3;
        ToggleHotkey = settings.ToggleHotkey;
        MuteHotkey = settings.MuteHotkey;
        InjectTextHotkey = settings.InjectTextHotkey;
        DisableInjectionOnFocusChange = settings.DisableInjectionOnFocusChange;
        AutoStartRecognition = settings.AutoStartRecognition;
        AlwaysOnTop = settings.AlwaysOnTop;
        PostProcessingEnabled = settings.PostProcessingEnabled;

        foreach (var rule in settings.PostProcessingRules)
            Rules.Add(rule);

        ScanModels();
    }

    // ---- Property change hooks ----

    partial void OnModelsRootPathChanged(string value)
    {
        ScanModels();
    }

    partial void OnSelectedModelChanged(string value)
    {
        OnPropertyChanged(nameof(ModelPath));
    }

    // ---- Commands ----

    [RelayCommand]
    private void Save()
    {
        var settings = BuildSettings();
        _settingsService.Save(settings);
        WasSaved = true;
        WeakReferenceMessenger.Default.Send(new SettingsSavedMessage(settings));
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke();

    [RelayCommand]
    private void AddRule() => Rules.Add(new PostProcessingRule { Name = "New rule" });

    [RelayCommand]
    private void DeleteRule(PostProcessingRule? rule)
    {
        if (rule is not null) Rules.Remove(rule);
    }

    [RelayCommand]
    private void OpenModelDownloader()
    {
        if (Views.ModelDownloaderWindow.OpenInstance is not null)
        {
            Views.ModelDownloaderWindow.OpenInstance.Activate();
            return;
        }

        var window = new Views.ModelDownloaderWindow
        {
            ModelsRootPath = ModelsRootPath
        };
        window.Closed += (_, _) =>
        {
            if (window.ViewModel.WasDownloaded && window.ViewModel.ResultPath is not null)
                ModelsRootPath = window.ViewModel.ResultPath;
        };
        window.Activate();
    }

    [RelayCommand]
    private async Task BrowseRoot()
    {
        var initialPath = Directory.Exists(ModelsRootPath) ? ModelsRootPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "modules", "asr"));

        var path = await FolderBrowser.ShowAsync("Select root folder with model subfolders", initialPath, OwnerWindowHandle);
        if (path is not null)
            ModelsRootPath = path;
    }

    // ---- Scan models ----

    private void ScanModels()
    {
        AvailableModels.Clear();
        if (string.IsNullOrEmpty(ModelsRootPath)) return;

        var root = ModelsRootPath;
        if (!Path.IsPathRooted(root))
            root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));

        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var configPath = Path.Combine(dir, "genai_config.json");
            if (File.Exists(configPath))
                AvailableModels.Add(Path.GetFileName(dir));
        }

        if (!string.IsNullOrEmpty(SelectedModel) && !AvailableModels.Contains(SelectedModel))
            SelectedModel = AvailableModels.Count > 0 ? AvailableModels[0] : "";

        OnPropertyChanged(nameof(ModelPath));
    }

    // ---- Build settings ----

    public AppSettings BuildSettings() => new()
    {
        ModelsRootPath = ModelsRootPath,
        SelectedModel = SelectedModel,
        ModelPath = ModelPath,
        ExecutionProvider = ExecutionProvider,
        Language = Language,
        UseVad = UseVad,
        NumBeams = Math.Max(1, NumBeams),
        RepetitionPenalty = RepetitionPenalty,
        AudioSource = AudioSource,
        TextInjectionMethod = TextInjectionMethod,
        StopOnAnyInput = StopOnAnyInput,
        IsTextInjectionEnabled = _original.IsTextInjectionEnabled,
        IsAutoScrollEnabled = _original.IsAutoScrollEnabled,
        SaveSessions = SaveSessions,
        SessionsPath = SessionsPath,
        SaveAudioMp3 = SaveAudioMp3,
        ToggleHotkey = ToggleHotkey,
        MuteHotkey = MuteHotkey,
        InjectTextHotkey = InjectTextHotkey,
        DisableInjectionOnFocusChange = DisableInjectionOnFocusChange,
        AutoStartRecognition = AutoStartRecognition,
        AlwaysOnTop = AlwaysOnTop,
        PostProcessingEnabled = PostProcessingEnabled,
        PostProcessingRules = Rules.ToList(),
        DownloaderRepoId = _original.DownloaderRepoId,
        DownloaderModelsRootPath = _original.DownloaderModelsRootPath,
        DownloaderSelectedFoldersRepoId = _original.DownloaderSelectedFoldersRepoId,
        DownloaderSelectedFolders = _original.DownloaderSelectedFolders.ToList(),
    };
}