using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

/// <summary>
/// ViewModel for the Settings window.
/// WinUI 3: async FolderPicker instead of SHBrowseForFolder.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _original;

    /// <summary>HWND of the parent window — needed for FolderPicker.</summary>
    public nint OwnerWindowHandle { get; set; }

    public SettingsViewModel(AppSettings settings)
    {
        _original = settings;

        // Clone for editing
        AvailableModels = new ObservableCollection<string>();
        ModelsRootPath = settings.ModelsRootPath;
        ModelPath = settings.ModelPath;
        SelectedModel = settings.SelectedModel;
        ExecutionProvider = settings.ExecutionProvider;
        Language = settings.Language;
        UseVad = settings.UseVad;
        NumBeams = settings.NumBeams;
        RepetitionPenalty = settings.RepetitionPenalty;

        // Set default models root if empty (data/Models next to the app)
        if (string.IsNullOrEmpty(ModelsRootPath))
        {
            ModelsRootPath = Services.AppPaths.ModelsDir;
        }

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
        Rules = new ObservableCollection<PostProcessingRule>(settings.PostProcessingRules);

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        AddRuleCommand = new RelayCommand(AddRule);
        DeleteRuleCommand = new RelayCommand<PostProcessingRule>(DeleteRule);
        OpenModelDownloaderCommand = new RelayCommand(OpenModelDownloader);
        BrowseRootCommand = new AsyncRelayCommand(BrowseRootAsync);

        // Initial scan
        ScanModels();
    }

    // ── Engine ──────────────────────────────────────
    private string _modelsRootPath = "";
    public string ModelsRootPath
    {
        get => _modelsRootPath;
        set { _modelsRootPath = value; OnPropertyChanged(); ScanModels(); }
    }

    private string _selectedModel = "";
    public string SelectedModel
    {
        get => _selectedModel;
        set { _selectedModel = value; OnPropertyChanged(); UpdateModelPath(); }
    }

    public string ModelPath { get; set; }
    public string ExecutionProvider { get; set; }
    public string Language { get; set; }
    public bool UseVad { get; set; }
    public int NumBeams { get; set; }
    public double RepetitionPenalty { get; set; }

    public ObservableCollection<string> AvailableModels { get; }

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

        // Keep current selection if still valid
        if (!string.IsNullOrEmpty(SelectedModel) && !AvailableModels.Contains(SelectedModel))
            SelectedModel = AvailableModels.Count > 0 ? AvailableModels[0] : "";

        UpdateModelPath();
    }

    private void UpdateModelPath()
    {
        ModelPath = (!string.IsNullOrEmpty(ModelsRootPath) && !string.IsNullOrEmpty(SelectedModel))
            ? Path.Combine(ModelsRootPath, SelectedModel)
            : "";
        OnPropertyChanged(nameof(ModelPath));
    }

    private async Task BrowseRootAsync()
    {
        var initialPath = Directory.Exists(ModelsRootPath) ? ModelsRootPath
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "modules", "asr"));

        var path = await FolderBrowser.ShowAsync("Select root folder with model subfolders", initialPath, OwnerWindowHandle);
        if (path is not null)
            ModelsRootPath = path;
    }

    // ── Capture ─────────────────────────────────────
    public string AudioSource { get; set; }

    // ── Injection ───────────────────────────────────
    public InjectionMethod TextInjectionMethod { get; set; }
    public bool StopOnAnyInput { get; set; }
    public bool DisableInjectionOnFocusChange { get; set; }
    public bool AutoStartRecognition { get; set; }
    public bool AlwaysOnTop { get; set; }

    // ── Sessions ────────────────────────────────────
    public bool SaveSessions { get; set; }
    public string SessionsPath { get; set; }
    public bool SaveAudioMp3 { get; set; }

    // ── Hotkey ─────────────────────────────────────
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+V";
    public string MuteHotkey { get; set; } = "Ctrl+Shift+M";
    public string InjectTextHotkey { get; set; } = "Ctrl+Shift+I";

    // ── Post-processing ─────────────────────────────
    public bool PostProcessingEnabled { get; set; }
    public ObservableCollection<PostProcessingRule> Rules { get; }

    // ── Commands ────────────────────────────────────
    public System.Windows.Input.ICommand SaveCommand { get; }
    public System.Windows.Input.ICommand CancelCommand { get; }
    public System.Windows.Input.ICommand AddRuleCommand { get; }
    public System.Windows.Input.ICommand DeleteRuleCommand { get; }
    public System.Windows.Input.ICommand OpenModelDownloaderCommand { get; }
    public System.Windows.Input.ICommand BrowseRootCommand { get; }

    public bool WasSaved { get; private set; }

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

    private void Save()
    {
        var settings = BuildSettings();
        SettingsService.Save(settings);
        WasSaved = true;
        RequestClose?.Invoke();
    }

    private void Cancel() => RequestClose?.Invoke();

    private void AddRule() => Rules.Add(new PostProcessingRule { Name = "New rule" });

    private void DeleteRule(PostProcessingRule? rule)
    {
        if (rule is not null) Rules.Remove(rule);
    }

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
                ModelsRootPath = window.ViewModel.ResultPath;  // This triggers ScanModels()
        };
        window.Activate();
    }

    public event Action? RequestClose;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
