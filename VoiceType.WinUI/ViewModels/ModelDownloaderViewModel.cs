using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using VoiceType.WinUI.Interfaces;
using VoiceType.WinUI.Models;
using VoiceType.WinUI.Services;

namespace VoiceType.WinUI.ViewModels;

public sealed class ModelOption
{
    public string Display { get; init; } = "";
    public string RepoId { get; init; } = "";
    public override string ToString() => Display;
}

public sealed partial class ModelDownloaderViewModel : ObservableObject, IDisposable
{
    private readonly IModelDownloaderService _service;
    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcher;
    private string? _resultModelPath;

    public nint OwnerWindowHandle { get; set; }

    // ---- Observable properties ----

    [ObservableProperty]
    private string _modelsRootPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isDownloading;

    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    private string _currentFile = "";

    [ObservableProperty]
    private string _fileRemaining = "";

    [ObservableProperty]
    private double _fileProgress;

    [ObservableProperty]
    private string _folderRemaining = "";

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private int _downloadedFiles;

    [ObservableProperty]
    private int _totalFiles;

    [ObservableProperty]
    private ModelOption? _selectedModel;

    public bool IsIdle => !IsDownloading;
    public string FileProgressDisplay => $"{FileProgress:F0}%";
    public string DownloadProgressDisplay => $"{DownloadProgress:F0}%";

    public string? ResultPath { get; private set; }
    public string? ResultModelPath { get; private set; }
    public bool WasDownloaded => ResultPath is not null;

    // ---- Predefined models ----

    public static List<ModelOption> AvailableModels { get; } =
    [
        new() { Display = "CPU (INT8) -- best quality, ~1020 MB", RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu" },
        new() { Display = "CPU (INT4) -- best perf/quality, ~760 MB", RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-cpu" },
        new() { Display = "CPU (FP32) -- full precision, ~2 GB",     RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-cpu" },
        new() { Display = "CPU (INT4, opset24, 0.56s) -- fast, low latency, ~749 MB", RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c056-cpu" },
        new() { Display = "CPU (INT4, opset24, 1.12s) -- best INT4 accuracy, ~749 MB", RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int4-opset24-c112-cpu" },
        new() { Display = "CPU (FP32, opset24, 0.56s) -- full precision, ~2 GB", RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-opset24-c056-cpu" },
        new() { Display = "CPU (FP32, opset24, 1.12s) -- max accuracy, ~2 GB",   RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-fp32-opset24-c112-cpu" },
    ];

    public List<ModelOption> ModelOptions => AvailableModels;

    // ---- Constructor ----

    public ModelDownloaderViewModel(
        IModelDownloaderService service,
        ISettingsService settingsService,
        DispatcherQueue dispatcher)
    {
        _service = service;
        _settingsService = settingsService;
        _dispatcher = dispatcher;

        var settings = settingsService.Load();
        ModelsRootPath = ResolveModelsRootPath(settings);

        SelectedModel = AvailableModels.FirstOrDefault(m =>
            m.RepoId == MainViewModel.RecommendedModelRepo)
            ?? AvailableModels.FirstOrDefault();

        _service.StatusChanged += s => _dispatcher.TryEnqueue(() => Status = s);
        _service.ProgressChanged += OnProgress;
        _service.Completed += OnCompleted;
    }

    // ---- Property change hooks ----

    partial void OnSelectedModelChanged(ModelOption? value)
    {
        DownloadProgress = 0;
        FileProgress = 0;
        Status = value is not null ? $"Selected: {value.Display}" : "Ready";
    }

    // ---- Commands ----

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        var model = SelectedModel;
        if (model is null) return;

        ResultPath = ResultModelPath = null;
        IsDownloading = true;
        FolderRemaining = "";
        FileRemaining = "";
        CurrentFile = "";
        FileProgress = 0;
        DownloadProgress = 0;
        DownloadedFiles = 0;
        TotalFiles = 0;

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

    private bool CanDownload() => SelectedModel is not null && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _service.Cancel();

    private bool CanCancel() => IsDownloading;

    [RelayCommand]
    private async Task BrowseRoot()
    {
        var initialPath = Directory.Exists(ModelsRootPath) ? ModelsRootPath
            : Services.AppPaths.DataRoot;
        var path = await FolderBrowser.ShowAsync("Select root folder for downloaded models", initialPath, OwnerWindowHandle);
        if (path is not null) ModelsRootPath = path;
    }

    // ---- Progress handlers ----

    private void OnProgress(DownloadProgress p)
    {
        _dispatcher.TryEnqueue(() =>
        {
            CurrentFile = p.CurrentFile;
            FileProgress = p.FileProgress;
            DownloadProgress = p.OverallProgress;
            DownloadedFiles = p.DownloadedFiles;
            TotalFiles = p.TotalFiles;

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
        _dispatcher.TryEnqueue(() =>
        {
            IsDownloading = false;
            if (ok)
            {
                ResultPath = ModelsRootPath;
                ResultModelPath = _resultModelPath;
                Status = "Download complete!";
            }
            else
            {
                ResultPath = ResultModelPath = null;
                Status = msg;
            }
        });
    }

    // ---- Helpers ----

    private static string ResolveModelsRootPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.ModelsRootPath) && Directory.Exists(settings.ModelsRootPath))
            return settings.ModelsRootPath;
        return Services.AppPaths.ModelsDir;
    }

    public void Dispose() => _service.Dispose();

    // ---- Legacy helpers (test compat) ----

    public static string ResolveDownloaderRepoId(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DownloaderRepoId)
            ? (AvailableModels.FirstOrDefault()?.RepoId ?? "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu")
            : settings.DownloaderRepoId;

    public static string ResolveDownloaderModelsRootPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DownloaderModelsRootPath)) return settings.DownloaderModelsRootPath;
        if (!string.IsNullOrWhiteSpace(settings.ModelsRootPath)) return settings.ModelsRootPath;
        return Services.AppPaths.ModelsDir;
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
}