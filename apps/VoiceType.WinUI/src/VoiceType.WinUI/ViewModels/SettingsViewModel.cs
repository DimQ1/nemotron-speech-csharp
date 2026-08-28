using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using SpeechLib.ModelDownload;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Messages;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly AppSettings _original;
    private bool _isApplyingLanguageMessage;

    public nint OwnerWindowHandle { get; set; }

    // ---- Observable properties ----

    [ObservableProperty]
    private string _modelsRootPath = "";

    [ObservableProperty]
    private string _selectedModel = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelPath))]
    private string _executionProvider = "cpu";

    /// <summary>Compute backend choices for the native translation engine.</summary>
    public IReadOnlyList<string> TranslationComputeBackendOptions { get; } = ["cpu", "gpu"];

    [ObservableProperty]
    private string _translationComputeBackend = "cpu";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelPath))]
    private string _language = "auto";

    [ObservableProperty]
    private bool _useVad = true;

    [ObservableProperty]
    private double _repetitionPenalty = 1.1;

    [ObservableProperty]
    private string _audioSource = "Mic";

    [ObservableProperty]
    private InjectionMethod _textInjectionMethod = InjectionMethod.InputSimulator;

    /// <summary>
    /// String representation of <see cref="TextInjectionMethod"/> for ComboBox binding.
    /// Keeps the enum in the model while letting XAML bind to string values.
    /// </summary>
    public string TextInjectionMethodString
    {
        get => TextInjectionMethod.ToString();
        set
        {
            if (Enum.TryParse<InjectionMethod>(value, out var method))
                TextInjectionMethod = method;
        }
    }

    [ObservableProperty]
    private bool _stopOnAnyInput = false;

    [ObservableProperty]
    private bool _disableInjectionOnFocusChange = true;

    [ObservableProperty]
    private bool _autoStartRecognition;

    [ObservableProperty]
    private bool _alwaysOnTop = true;

    [ObservableProperty]
    private bool _clearTextOnModelOrSessionChange = true;

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

    // ---- About info (read-only) ----

    /// <summary>Application version from package identity or assembly.</summary>
    public string AppVersion => GetAppVersion();

    /// <summary>Application display name.</summary>
    public string AppDisplayName => "VoiceType";

    /// <summary>Package full name (for Store/MSIX) or "Unpackaged" for dev.</summary>
    public string PackageFullName => GetPackageFullName();

    /// <summary>.NET runtime version.</summary>
    public string DotNetVersion => Environment.Version.ToString();

    /// <summary>OS version.</summary>
    public string OsVersion => Environment.OSVersion.ToString();

    /// <summary>Number of logical CPU cores.</summary>
    public int CpuCores => Environment.ProcessorCount;

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
        TranslationComputeBackend = NormalizeTranslationComputeBackend(settings.TranslationComputeBackend);
        _language = settings.Language;
        UseVad = settings.UseVad;
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
        ClearTextOnModelOrSessionChange = settings.ClearTextOnModelOrSessionChange;
        PostProcessingEnabled = settings.PostProcessingEnabled;

        foreach (var rule in settings.PostProcessingRules)
            Rules.Add(rule);

        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (r, m) =>
        {
            if (string.Equals(Language, m.Value, StringComparison.Ordinal))
                return;

            _isApplyingLanguageMessage = true;
            try
            {
                Language = m.Value;
            }
            finally
            {
                _isApplyingLanguageMessage = false;
            }
        });

        // Refresh the model list when a download completes, so a newly
        // downloaded model appears in settings without reopening the dialog.
        WeakReferenceMessenger.Default.Register<ModelDownloadedMessage>(this, (r, m) =>
        {
            ScanModels();
        });

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

    partial void OnLanguageChanged(string value)
    {
        if (!_isApplyingLanguageMessage)
            WeakReferenceMessenger.Default.Send(new LanguageChangedMessage(value));
    }

    // ---- Commands ----

    [RelayCommand]
    private async Task Save()
    {
        if (WasSaved)
            return;

        var settings = BuildSettings();
        AppSettings? savedSettings = null;
        await Task.Run(() => _settingsService.Update(current =>
        {
            ApplyEditableSettings(current, settings);
            savedSettings = current.Clone();
        }));
        WasSaved = true;
        WeakReferenceMessenger.Default.Send(new SettingsSavedMessage(savedSettings ?? settings));
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
        App.MainWindow?.TrackChildWindow(window);
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

    // ---- About helpers ----

    private static string GetAppVersion()
    {
        try
        {
            // Try MSIX package version first (Store / installed)
            var package = Windows.ApplicationModel.Package.Current;
            var v = package.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}";
        }
        catch
        {
            // Fallback to assembly version (dev / unpackaged)
            var asm = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return asm is not null ? $"{asm.Major}.{asm.Minor}.{asm.Build}.{asm.Revision}" : "Unknown";
        }
    }

    private static string GetPackageFullName()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current.Id.FullName;
        }
        catch
        {
            return "Unpackaged (dev mode)";
        }
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

        foreach (var name in ModelFolderScanner.ScanModelFolderNames(root))
            AvailableModels.Add(name);

        if (string.IsNullOrEmpty(SelectedModel) && AvailableModels.Count == 1)
            SelectedModel = AvailableModels[0];

        OnPropertyChanged(nameof(ModelPath));
    }

    // ---- Build settings ----

    private static void ApplyEditableSettings(AppSettings target, AppSettings source)
    {
        target.ModelsRootPath = source.ModelsRootPath;
        target.SelectedModel = source.SelectedModel;
        target.ModelPath = source.ModelPath;
        target.ExecutionProvider = source.ExecutionProvider;
        target.TranslationEnabled = source.TranslationEnabled;
        target.TranslationTargetLanguage = source.TranslationTargetLanguage;
        target.TranslationComputeBackend = source.TranslationComputeBackend;
        target.Language = source.Language;
        target.UseVad = source.UseVad;
        target.RepetitionPenalty = source.RepetitionPenalty;
        target.AudioSource = source.AudioSource;
        target.TextInjectionMethod = source.TextInjectionMethod;
        target.StopOnAnyInput = source.StopOnAnyInput;
        target.DisableInjectionOnFocusChange = source.DisableInjectionOnFocusChange;
        target.AutoStartRecognition = source.AutoStartRecognition;
        target.AlwaysOnTop = source.AlwaysOnTop;
        target.ClearTextOnModelOrSessionChange = source.ClearTextOnModelOrSessionChange;
        target.SaveSessions = source.SaveSessions;
        target.SessionsPath = source.SessionsPath;
        target.SaveAudioMp3 = source.SaveAudioMp3;
        target.ToggleHotkey = source.ToggleHotkey;
        target.MuteHotkey = source.MuteHotkey;
        target.InjectTextHotkey = source.InjectTextHotkey;
        target.PostProcessingEnabled = source.PostProcessingEnabled;
        target.PostProcessingRules = source.PostProcessingRules
            .Select(rule => new PostProcessingRule
            {
                Name = rule.Name,
                Pattern = rule.Pattern,
                Replacement = rule.Replacement,
                Enabled = rule.Enabled
            })
            .ToList();
    }

    public AppSettings BuildSettings() => new()
    {
        ModelsRootPath = ModelsRootPath,
        SelectedModel = SelectedModel,
        ModelPath = ModelPath,
        ExecutionProvider = ExecutionProvider,
        TranslationEnabled = _original.TranslationEnabled,
        TranslationTargetLanguage = _original.TranslationTargetLanguage,
        TranslationComputeBackend = TranslationComputeBackend,
        Language = Language,
        UseVad = UseVad,
        RepetitionPenalty = RepetitionPenalty,
        AudioSource = AudioSource,
        FirstRunCompleted = _original.FirstRunCompleted,
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
        ClearTextOnModelOrSessionChange = ClearTextOnModelOrSessionChange,
        PostProcessingEnabled = PostProcessingEnabled,
        PostProcessingRules = Rules.ToList(),
        DownloaderRepoId = _original.DownloaderRepoId,
        DownloaderModelsRootPath = _original.DownloaderModelsRootPath,
        DownloaderSelectedFoldersRepoId = _original.DownloaderSelectedFoldersRepoId,
        DownloaderSelectedFolders = _original.DownloaderSelectedFolders.ToList(),
        MicVolume = _original.MicVolume,
        LoopbackVolume = _original.LoopbackVolume,
    };

    private static string NormalizeTranslationComputeBackend(string? backend) =>
        string.Equals(backend, "gpu", StringComparison.OrdinalIgnoreCase) ? "gpu" : "cpu";
}