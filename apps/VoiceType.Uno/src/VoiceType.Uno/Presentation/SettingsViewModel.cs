using CommunityToolkit.Mvvm.ComponentModel;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation;

/// <summary>Editable settings snapshot used by the cross-platform UNO settings dialog.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _original;

    public static IReadOnlyList<string> DefaultLanguageOptions { get; } =
    [
        "auto", "en", "ru", "de", "fr", "es", "zh", "ja", "ko", "pt",
        "it", "ar", "hi", "tr", "uk", "pl", "nl"
    ];

    public static IReadOnlyList<string> DefaultAudioSourceOptions { get; } = ["Mic", "Loopback", "Mix"];
    public static IReadOnlyList<string> DefaultExecutionProviderOptions { get; } = ["cpu", "follow_config"];
    public static IReadOnlyList<string> DefaultTranslationBackendOptions { get; } = ["native", "http"];
    public static IReadOnlyList<string> DefaultTranslationComputeBackendOptions { get; } = ["cpu", "gpu"];

    public IReadOnlyList<string> LanguageOptions => DefaultLanguageOptions;
    public IReadOnlyList<string> AudioSourceOptions => DefaultAudioSourceOptions;
    public IReadOnlyList<string> ExecutionProviderOptions => DefaultExecutionProviderOptions;
    public IReadOnlyList<string> TranslationBackendOptions => DefaultTranslationBackendOptions;
    public IReadOnlyList<string> TranslationComputeBackendOptions => DefaultTranslationComputeBackendOptions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelPath))]
    private string _modelsRootPath = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelPath))]
    private string _selectedModel = "";

    [ObservableProperty]
    private string _executionProvider = "cpu";

    [ObservableProperty]
    private string _language = "auto";

    [ObservableProperty]
    private string _audioSource = "Mic";

    [ObservableProperty]
    private bool _useVad = true;

    [ObservableProperty]
    private int _numBeams = 1;

    [ObservableProperty]
    private double _repetitionPenalty = 1.1;

    [ObservableProperty]
    private bool _isTextInjectionEnabled = true;

    [ObservableProperty]
    private string _pasteChord = "Ctrl+V";

    [ObservableProperty]
    private bool _translationEnabled;

    [ObservableProperty]
    private string _translationBackend = "native";

    [ObservableProperty]
    private string _translationComputeBackend = "cpu";

    [ObservableProperty]
    private string _translationTargetLanguage = "ru";

    [ObservableProperty]
    private string _translationServerUrl = "http://localhost:9379";

    [ObservableProperty]
    private string _translationSystemPrompt = "";

    [ObservableProperty]
    private string _toggleHotkey = "Ctrl+Shift+V";

    [ObservableProperty]
    private string _muteHotkey = "Ctrl+Shift+M";

    [ObservableProperty]
    private string _injectTextHotkey = "Ctrl+Shift+I";

    /// <summary>True when the native .litertlm translation model is present on disk.</summary>
    public bool IsNativeModelDownloaded => TranslationModelInfo.IsDownloaded;

    public string NativeModelStatus => TranslationModelInfo.IsDownloaded
        ? $"Downloaded: {TranslationModelInfo.LocalModelPath}"
        : $"Not downloaded — will fall back to the HTTP server. ({TranslationModelInfo.FileName}, ~2.6 GB)";

    /// <summary>Re-evaluates native model presence after a download completes.</summary>
    public void NotifyNativeModelChanged()
    {
        OnPropertyChanged(nameof(IsNativeModelDownloaded));
        OnPropertyChanged(nameof(NativeModelStatus));
    }

    [ObservableProperty]
    private bool _isAutoScrollEnabled = true;

    [ObservableProperty]
    private bool _alwaysOnTop = true;

    [ObservableProperty]
    private bool _autoStartRecognition;

    [ObservableProperty]
    private bool _clearTextOnModelOrSessionChange = true;

    [ObservableProperty]
    private bool _isDownloadingModel;

    [ObservableProperty]
    private string _downloadStatus = "";

    public bool CanDownloadModel => !IsDownloadingModel;

    partial void OnIsDownloadingModelChanged(bool value) => OnPropertyChanged(nameof(CanDownloadModel));

    public List<string> AvailableModels { get; } = [];

    /// <summary>ASR model variants published on Hugging Face (see AsrModelCatalog).</summary>
    public IReadOnlyList<AsrModelCatalogEntry> AsrModelOptions => AsrModelCatalog.Models;

    /// <summary>The Hugging Face model variant the Download button will fetch.</summary>
    [ObservableProperty]
    private AsrModelCatalogEntry _selectedAsrModel = AsrModelCatalog.Recommended;

    public string ModelPath => !string.IsNullOrWhiteSpace(ModelsRootPath)
        && !string.IsNullOrWhiteSpace(SelectedModel)
        ? Path.Combine(ModelsRootPath, SelectedModel)
        : string.Empty;

    public SettingsViewModel(AppSettings settings)
    {
        _original = settings.Clone();
        ModelsRootPath = string.IsNullOrWhiteSpace(settings.ModelsRootPath)
            ? AppPaths.ModelsDir
            : settings.ModelsRootPath;
        SelectedModel = settings.SelectedModel;
        ExecutionProvider = settings.ExecutionProvider;
        Language = settings.Language;
        AudioSource = settings.AudioSource;
        UseVad = settings.UseVad;
        NumBeams = settings.NumBeams;
        RepetitionPenalty = settings.RepetitionPenalty;
        IsTextInjectionEnabled = settings.IsTextInjectionEnabled;
        PasteChord = string.IsNullOrWhiteSpace(settings.PasteChord) ? "Ctrl+V" : settings.PasteChord;
        TranslationEnabled = settings.TranslationEnabled;
        TranslationBackend = string.IsNullOrWhiteSpace(settings.TranslationBackend) ? "native" : settings.TranslationBackend;
        TranslationComputeBackend = string.IsNullOrWhiteSpace(settings.TranslationComputeBackend) ? "cpu" : settings.TranslationComputeBackend;
        TranslationTargetLanguage = settings.TranslationTargetLanguage;
        TranslationServerUrl = settings.TranslationServerUrl;
        TranslationSystemPrompt = settings.TranslationSystemPrompt;
        ToggleHotkey = settings.ToggleHotkey;
        MuteHotkey = settings.MuteHotkey;
        InjectTextHotkey = settings.InjectTextHotkey;
        IsAutoScrollEnabled = settings.IsAutoScrollEnabled;
        AlwaysOnTop = settings.AlwaysOnTop;
        AutoStartRecognition = settings.AutoStartRecognition;
        ClearTextOnModelOrSessionChange = settings.ClearTextOnModelOrSessionChange;
        ScanModels();
    }

    partial void OnModelsRootPathChanged(string value) => ScanModels();

    private void ScanModels()
    {
        AvailableModels.Clear();
        if (!Directory.Exists(ModelsRootPath))
            return;

        foreach (var directory in Directory.GetDirectories(ModelsRootPath).OrderBy(path => path))
        {
            if (File.Exists(Path.Combine(directory, "genai_config.json")))
                AvailableModels.Add(Path.GetFileName(directory));
        }

        if (string.IsNullOrWhiteSpace(SelectedModel) && AvailableModels.Count == 1)
            SelectedModel = AvailableModels[0];
    }

    public AppSettings BuildSettings()
    {
        var settings = _original.Clone();
        settings.ModelsRootPath = ModelsRootPath.Trim();
        settings.SelectedModel = SelectedModel.Trim();
        settings.ModelPath = ModelPath;
        settings.ExecutionProvider = ExecutionProvider;
        settings.Language = Language;
        settings.AudioSource = AudioSource;
        settings.UseVad = UseVad;
        settings.NumBeams = Math.Max(1, NumBeams);
        settings.RepetitionPenalty = Math.Max(1, RepetitionPenalty);
        settings.IsTextInjectionEnabled = IsTextInjectionEnabled;
        settings.PasteChord = string.IsNullOrWhiteSpace(PasteChord) ? "Ctrl+V" : PasteChord.Trim();
        settings.TranslationEnabled = TranslationEnabled;
        settings.TranslationBackend = string.IsNullOrWhiteSpace(TranslationBackend) ? "native" : TranslationBackend.Trim();
        settings.TranslationComputeBackend = string.IsNullOrWhiteSpace(TranslationComputeBackend) ? "cpu" : TranslationComputeBackend.Trim();
        settings.TranslationTargetLanguage = string.IsNullOrWhiteSpace(TranslationTargetLanguage) ? "ru" : TranslationTargetLanguage.Trim();
        settings.TranslationServerUrl = string.IsNullOrWhiteSpace(TranslationServerUrl) ? "http://localhost:9379" : TranslationServerUrl.Trim();
        settings.TranslationSystemPrompt = TranslationSystemPrompt?.Trim() ?? "";
        settings.IsAutoScrollEnabled = IsAutoScrollEnabled;
        settings.AlwaysOnTop = AlwaysOnTop;
        settings.AutoStartRecognition = AutoStartRecognition;
        settings.ClearTextOnModelOrSessionChange = ClearTextOnModelOrSessionChange;
        settings.ToggleHotkey = string.IsNullOrWhiteSpace(ToggleHotkey) ? "" : ToggleHotkey.Trim();
        settings.MuteHotkey = string.IsNullOrWhiteSpace(MuteHotkey) ? "" : MuteHotkey.Trim();
        settings.InjectTextHotkey = string.IsNullOrWhiteSpace(InjectTextHotkey) ? "" : InjectTextHotkey.Trim();
        return settings;
    }
}
