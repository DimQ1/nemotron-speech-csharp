using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using VoiceType.Models;
using VoiceType.Services;

namespace VoiceType.ViewModels;

/// <summary>
/// ViewModel for the Settings window.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _original;

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

        // Set default models root if empty
        if (string.IsNullOrEmpty(ModelsRootPath))
        {
            var defaultRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models-onnx");
            ModelsRootPath = Path.GetFullPath(defaultRoot);
        }

        AudioSource = settings.AudioSource;
        TextInjectionMethod = settings.TextInjectionMethod;
        StopOnAnyInput = settings.StopOnAnyInput;

        SaveSessions = settings.SaveSessions;
        SessionsPath = settings.SessionsPath;
        SaveAudioMp3 = settings.SaveAudioMp3;

        ToggleHotkey = settings.ToggleHotkey;
        PostProcessingEnabled = settings.PostProcessingEnabled;
        Rules = new ObservableCollection<PostProcessingRule>(settings.PostProcessingRules);

        SaveCommand = new RelayCommand(Save);
        CancelCommand = new RelayCommand(Cancel);
        AddRuleCommand = new RelayCommand(AddRule);
        DeleteRuleCommand = new RelayCommand<PostProcessingRule>(DeleteRule);
        OpenModelDownloaderCommand = new RelayCommand(OpenModelDownloader);
        BrowseRootCommand = new RelayCommand(BrowseRoot);

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

    private void BrowseRoot()
    {
        var path = FolderBrowser.Show("Select root folder with model subfolders",
            Directory.Exists(ModelsRootPath) ? ModelsRootPath
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models-onnx")));
        if (path is not null)
            ModelsRootPath = path;
    }

    // ── Capture ─────────────────────────────────────
    public string AudioSource { get; set; }

    // ── Injection ───────────────────────────────────
    public InjectionMethod TextInjectionMethod { get; set; }
    public bool StopOnAnyInput { get; set; }

    // ── Sessions ────────────────────────────────────
    public bool SaveSessions { get; set; }
    public string SessionsPath { get; set; }
    public bool SaveAudioMp3 { get; set; }

    // ── Hotkey ─────────────────────────────────────
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+V";

    // ── Post-processing ─────────────────────────────
    public bool PostProcessingEnabled { get; set; }
    public ObservableCollection<PostProcessingRule> Rules { get; }

    // ── Commands ────────────────────────────────────
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddRuleCommand { get; }
    public ICommand DeleteRuleCommand { get; }
    public ICommand OpenModelDownloaderCommand { get; }
    public ICommand BrowseRootCommand { get; }

    public bool WasSaved { get; private set; }

    public AppSettings BuildSettings() => new()
    {
        ModelsRootPath = ModelsRootPath,
        SelectedModel = SelectedModel,
        ModelPath = ModelPath,
        ExecutionProvider = ExecutionProvider,
        Language = Language,
        UseVad = UseVad,
        AudioSource = AudioSource,
        TextInjectionMethod = TextInjectionMethod,
        StopOnAnyInput = StopOnAnyInput,
        SaveSessions = SaveSessions,
        SessionsPath = SessionsPath,
        SaveAudioMp3 = SaveAudioMp3,
        ToggleHotkey = ToggleHotkey,
        PostProcessingEnabled = PostProcessingEnabled,
        PostProcessingRules = Rules.ToList(),
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
        var window = new Views.ModelDownloaderWindow
        {
            Owner = System.Windows.Application.Current.MainWindow,
            ModelsRootPath = ModelsRootPath
        };
        window.ShowDialog();
        if (window.WasDownloaded && window.ResultPath is not null)
            ModelsRootPath = window.ResultPath;  // This triggers ScanModels()
    }

    public event Action? RequestClose;
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
