using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using VoiceType.Models;
using VoiceType.Services;

namespace VoiceType.ViewModels;

/// <summary>Predefined downloadable model configuration.</summary>
public sealed class ModelOption
{
    public string Display { get; init; } = "";
    public string RepoId { get; init; } = "";
    public override string ToString() => Display;
}

public sealed class ModelDownloaderViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ModelDownloaderService _service = new();
    private string _modelsRootPath = string.Empty;
    private string? _resultModelPath;
    private bool _isDownloading;

    public ModelDownloaderViewModel()
    {
        var settings = SettingsService.Load();
        ModelsRootPath = ResolveModelsRootPath(settings);
        SelectedModel = AvailableModels.FirstOrDefault();

        DownloadCommand = new AsyncRelayCommand(DownloadModel, () => SelectedModel is not null && !IsDownloading);
        CancelCommand = new RelayCommand(() => _service.Cancel(), () => IsDownloading);
        BrowseRootCommand = new RelayCommand(BrowseRoot);

        _service.StatusChanged += s => Dispatcher.CurrentDispatcher.BeginInvoke(() => Status = s);
        _service.ProgressChanged += OnProgress;
        _service.Completed += OnCompleted;
    }

    // ── Predefined model repos ───────────────────────
    public static List<ModelOption> AvailableModels { get; } =
    [
        new() { Display = "CPU (INT8) — best quality, ~1020 MB", RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu" },
        new() { Display = "CPU (INT4) — best perf/quality, ~760 MB", RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-cpu" },
        new() { Display = "CPU (FP32) — full precision, ~2 GB",     RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-cpu" },
    ];

    // ── Properties ───────────────────────────────────
    public string ModelsRootPath
    {
        get => _modelsRootPath;
        set { _modelsRootPath = value; OnPropertyChanged(); }
    }

    private ModelOption? _selectedModel;
    public ModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetProperty(ref _selectedModel, value)) return;
            DownloadProgress = 0; FileProgress = 0;
            Status = value is not null ? $"Selected: {value.Display}" : "Ready";
        }
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        set { if (SetProperty(ref _isDownloading, value)) OnPropertyChanged(nameof(IsIdle)); }
    }
    public bool IsIdle => !IsDownloading;

    private string _status = "Ready";
    public string Status { get => _status; set => SetProperty(ref _status, value); }

    // ── Per-file progress ────────────────────────────
    private string _currentFile = "";
    public string CurrentFile { get => _currentFile; set => SetProperty(ref _currentFile, value); }
    private string _fileRemaining = "";
    public string FileRemaining { get => _fileRemaining; set => SetProperty(ref _fileRemaining, value); }
    private double _fileProgress;
    public double FileProgress { get => _fileProgress; set { if (SetProperty(ref _fileProgress, value)) OnPropertyChanged(nameof(FileProgressDisplay)); } }
    public string FileProgressDisplay => $"{FileProgress:F0}%";

    // ── Total progress ────────────────────────────────
    private string _folderRemaining = "";
    public string FolderRemaining { get => _folderRemaining; set => SetProperty(ref _folderRemaining, value); }
    private double _downloadProgress;
    public double DownloadProgress { get => _downloadProgress; set { if (SetProperty(ref _downloadProgress, value)) OnPropertyChanged(nameof(DownloadProgressDisplay)); } }
    public string DownloadProgressDisplay => $"{DownloadProgress:F0}%";
    private int _downloadedFiles;
    public int DownloadedFiles { get => _downloadedFiles; set => SetProperty(ref _downloadedFiles, value); }
    private int _totalFiles;
    public int TotalFiles { get => _totalFiles; set => SetProperty(ref _totalFiles, value); }

    public string? ResultPath { get; private set; }
    public string? ResultModelPath { get; private set; }
    public bool WasDownloaded => ResultPath is not null;

    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand BrowseRootCommand { get; }

    // ── Download (direct, no scan needed) ────────────
    private async Task DownloadModel()
    {
        var model = SelectedModel;
        if (model is null) return;

        ResultPath = ResultModelPath = null;
        IsDownloading = true;
        FolderRemaining = ""; FileRemaining = "";
        CurrentFile = ""; FileProgress = 0; DownloadProgress = 0;
        DownloadedFiles = 0; TotalFiles = 0;

        var subfolder = model.RepoId[(model.RepoId.LastIndexOf('/') + 1)..];
        _resultModelPath = Path.Combine(ModelsRootPath, subfolder);

        try
        {
            await _service.DownloadModelRepo(model.RepoId, subfolder, ModelsRootPath);
        }
        catch (OperationCanceledException)
        {
            Status = "Download cancelled";
            IsDownloading = false;
        }
        catch (Exception ex)
        {
            Status = $"Download error: {ex.Message}";
            IsDownloading = false;
        }
    }

    private void OnProgress(DownloadProgress p)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentFile = p.CurrentFile;
            FileProgress = p.FileProgress;
            DownloadProgress = p.OverallProgress;
            DownloadedFiles = p.DownloadedFiles;
            TotalFiles = p.TotalFiles;

            // File remaining: need known file size — report percentage instead
            if (p.FileProgress > 0)
                FileRemaining = $"{100 - p.FileProgress:F0}% left";
            else
                FileRemaining = "";

            if (p.OverallProgress > 0)
                FolderRemaining = $"{100 - p.OverallProgress:F0}% remaining";
            else
                FolderRemaining = "";
        });
    }

    private void OnCompleted(bool ok, string msg)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            IsDownloading = false;
            if (ok)
            {
                ResultPath = ModelsRootPath;
                ResultModelPath = _resultModelPath;
                Status = "✅ Download complete!";
            }
            else
            {
                ResultPath = ResultModelPath = null;
                Status = $"❌ {msg}";
            }
        });
    }

    // ── Helpers ──────────────────────────────────────
    private void BrowseRoot()
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        var hwnd = owner is not null ? new WindowInteropHelper(owner).Handle : IntPtr.Zero;
        var path = FolderBrowser.Show("Select root folder for downloaded models",
            Directory.Exists(ModelsRootPath) ? ModelsRootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), hwnd);
        if (path is not null) ModelsRootPath = path;
    }

    private static string ResolveModelsRootPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ModelsRootPath) && Directory.Exists(settings.ModelsRootPath))
            return settings.ModelsRootPath;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceType", "Models");
    }

    public void Dispose() { _service.Dispose(); }

    // ── Legacy helper stubs (test compat) ────────────
    public static string ResolveDownloaderRepoId(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DownloaderRepoId)
            ? (AvailableModels.FirstOrDefault()?.RepoId ?? "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu")
            : settings.DownloaderRepoId;

    public static string ResolveDownloaderModelsRootPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DownloaderModelsRootPath)) return settings.DownloaderModelsRootPath;
        if (!string.IsNullOrWhiteSpace(settings.ModelsRootPath)) return settings.ModelsRootPath;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceType", "Models");
    }

    public static string ResolveDownloaderSelectedFoldersRepoId(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DownloaderSelectedFoldersRepoId) ? string.Empty : settings.DownloaderSelectedFoldersRepoId;

    public static HashSet<string> ResolveDownloaderSelectedFolders(AppSettings settings) =>
        settings.DownloaderSelectedFolders
            .Where(f => f is not null).Select(f => f.Trim())
            .Where(f => f.Length > 0 || f == string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void PersistDownloaderSettings(AppSettings settings, string repoId, string modelsRootPath)
    {
        settings.DownloaderRepoId = repoId.Trim();
        settings.DownloaderModelsRootPath = modelsRootPath.Trim();
    }

    public static void PersistDownloaderFolderSelection(AppSettings settings, string repoId, IEnumerable<string> selectedFolderKeys)
    {
        settings.DownloaderSelectedFoldersRepoId = repoId.Trim();
        settings.DownloaderSelectedFolders = selectedFolderKeys
            .Select(f => f.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static HashSet<string> CaptureSelectedFolderKeys(IEnumerable<HfFolder> folders) =>
        folders.Where(f => f.Selected).Select(f => f.SubfolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void ApplySelectedFolders(IEnumerable<HfFolder> folders, ISet<string> selectedFolderKeys)
    {
        foreach (var folder in folders)
            folder.Selected = selectedFolderKeys.Contains(folder.SubfolderName);
    }

    public static string? TryResolveCustomResultModelPath(IReadOnlyList<HfFolder> folders, string repoId, string modelsRootPath)
    {
        var selectedFolders = folders.Where(f => f.Selected).ToList();
        if (selectedFolders.Count != 1) return null;
        var subfolder = string.IsNullOrEmpty(selectedFolders[0].SubfolderName)
            ? repoId[(repoId.LastIndexOf('/') + 1)..]
            : selectedFolders[0].SubfolderName;
        return Path.Combine(modelsRootPath, subfolder);
    }

    public static string ParseRepoId(string input)
    {
        var s = input.Trim().TrimEnd('/');
        if (s.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase)) s = s["https://huggingface.co/".Length..];
        else if (s.StartsWith("huggingface.co/", StringComparison.OrdinalIgnoreCase)) s = s["huggingface.co/".Length..];
        var parts = s.Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : s;
    }

    // ── INotifyPropertyChanged ───────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(n); return true;
    }
}