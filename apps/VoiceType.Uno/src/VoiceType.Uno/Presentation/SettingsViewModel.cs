using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SpeechLib.ModelDownload;
using SpeechLib.ModelFormats;
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

    /// <summary>
    /// Compute providers offered in the settings dialog. The two WebGPU entries differ
    /// by Dawn power preference, which is the supported way to pick the physical
    /// adapter ("hp" = discrete GPU, "lp" = integrated GPU); see
    /// docs/research/onnx/webgpu-ep-evaluation.md.
    /// </summary>
    public static IReadOnlyList<string> DefaultExecutionProviderOptions { get; } = ["cpu", "webgpu:hp", "webgpu:lp", "follow_config"];
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
    [NotifyPropertyChangedFor(nameof(ModelPath), nameof(ActiveModelSummary))]
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

    /// <summary>Model folders under <see cref="ModelsRootPath"/>. Observable so the ComboBox follows rescans.</summary>
    public ObservableCollection<string> AvailableModels { get; } = [];

    /// <summary>
    /// Friendly description of the model the app will use: the catalog title for
    /// known variants, the raw folder name for custom models.
    /// </summary>
    public string ActiveModelSummary => string.IsNullOrWhiteSpace(SelectedModel)
        ? "No model selected — open the model manager to download one."
        : $"{ModelCatalog.DescribeFolder(SelectedModel)} ({SelectedModel})";

    public string ModelPath => !string.IsNullOrWhiteSpace(ModelsRootPath)
        && !string.IsNullOrWhiteSpace(SelectedModel)
        ? ModelPathResolver.LocateModelFolder(ModelsRootPath.Trim(), SelectedModel)
        : string.Empty;

    // ── Model storage block (one root, one sub-folder per kind) ────────────

    public string StorageTitle => ModelStorageText.Title;
    public string StorageRootLabel => ModelStorageText.RootLabel;
    public string StorageExplanation => ModelStorageText.Explanation;

    [ObservableProperty]
    private string _storageSpeechLine = "";

    [ObservableProperty]
    private string _storageLanguageLine = "";

    [ObservableProperty]
    private string _storageSpeakerLine = "";

    [ObservableProperty]
    private string _storageTotalLine = "";

    /// <summary>Shown when the folder differs from the saved one, or after a start-up move that could not finish.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStorageNote))]
    private string _storageNote = "";

    public bool HasStorageNote => !string.IsNullOrEmpty(StorageNote);

    /// <summary>The storage folder as saved (before this dialog), for detecting a change.</summary>
    public string OriginalModelsRoot => ModelStorageLayout.Resolve(_original).Root;

    /// <summary>True when the folder in the box is another folder than the saved one.</summary>
    public bool IsModelsRootChanged
    {
        get
        {
            try
            {
                return !string.IsNullOrWhiteSpace(ModelsRootPath)
                    && !string.Equals(
                        Path.GetFullPath(ModelsRootPath.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        OriginalModelsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        ModelStorageLayout.PathComparison);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                return false;
            }
        }
    }

    /// <summary>Re-measures the folder in the box (call after editing the path, Browse or a download).</summary>
    public void RefreshStorage()
    {
        ModelStorageSummary summary;
        try
        {
            summary = ModelStorageScanner.Summarize(new ModelStorageLayout(string.IsNullOrWhiteSpace(ModelsRootPath) ? AppPaths.ModelsDir : ModelsRootPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            StorageSpeechLine = StorageLanguageLine = StorageSpeakerLine = "";
            StorageTotalLine = "This folder cannot be used.";
            return;
        }

        StorageSpeechLine = summary.Line(ModelKind.Speech).Text;
        StorageLanguageLine = summary.Line(ModelKind.Language).Text;
        StorageSpeakerLine = summary.Line(ModelKind.Speaker).Text;
        StorageTotalLine = summary.TotalText;
        StorageNote = IsModelsRootChanged
            ? ModelStorageText.ChangeRootNote
            : !string.IsNullOrWhiteSpace(_original.PendingModelsRoot)
                ? ModelStorageText.PendingMoveNote(_original.PendingModelsRoot)
                : _original.ModelStorageNote;
    }

    public SettingsViewModel(AppSettings settings)
    {
        _original = settings.Clone();
        ModelsRootPath = ModelStorageLayout.Resolve(settings).Root;
        SelectedModel = settings.SelectedModel;
        ExecutionProvider = settings.ExecutionProvider;
        Language = settings.Language;
        AudioSource = settings.AudioSource;
        UseVad = settings.UseVad;
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
        RefreshStorage();
    }

    partial void OnModelsRootPathChanged(string value) => ScanModels();

    /// <summary>Re-scans the models root after a download completes, so the new
    /// model appears in settings without reopening the dialog.</summary>
    public void RefreshModels() => ScanModels();

    private void ScanModels()
    {
        // Clearing the bound collection drops the ComboBox selection (TwoWay binding
        // writes null back), so remember the choice and restore it after the rescan.
        var selected = SelectedModel;
        AvailableModels.Clear();
        if (!string.IsNullOrWhiteSpace(ModelsRootPath))
        {
            // <root>/asr plus the flat root of older versions (and any folder the user copied into either).
            var layout = new ModelStorageLayout(ModelsRootPath);
            foreach (var name in layout.AsrScanRoots.SelectMany(ModelFolderScanner.ScanModelFolderNames)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
                AvailableModels.Add(name);
        }

        if (!string.IsNullOrWhiteSpace(selected) && AvailableModels.Contains(selected))
            SelectedModel = selected;
        else if (AvailableModels.Count == 1)
            SelectedModel = AvailableModels[0];
        else
            SelectedModel = "";
    }

    public AppSettings BuildSettings()
    {
        var settings = _original.Clone();
        settings.ModelsRootPath = ModelsRootPath.Trim();
        settings.SelectedModel = (SelectedModel ?? "").Trim();
        settings.ModelPath = ModelPath;
        settings.ExecutionProvider = ExecutionProvider;
        settings.Language = Language;
        settings.AudioSource = AudioSource;
        settings.UseVad = UseVad;
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
