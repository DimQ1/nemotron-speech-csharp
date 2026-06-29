using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VoiceType.Services;

namespace VoiceType.ViewModels;

/// <summary>
/// ViewModel for the Model Downloader window.
/// </summary>
public sealed class ModelDownloaderViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ModelDownloaderService _downloader = new();

    public ModelDownloaderViewModel()
    {
        RepoId = "DimQ1/nemotron-speech-onnx";
        DownloadPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceType", "Models", "nemotron-speech-onnx");

        FetchFilesCommand = new RelayCommand(async () => await FetchFiles());
        DownloadSelectedCommand = new RelayCommand(async () => await DownloadSelected(),
            () => Files.Count > 0 && !IsDownloading);
        DownloadAllCommand = new RelayCommand(async () => await DownloadAll(),
            () => Files.Count > 0 && !IsDownloading);
        CancelCommand = new RelayCommand(() => _downloader.Cancel(), () => IsDownloading);
        SelectAllCommand = new RelayCommand(() => { foreach (var f in Files) f.Selected = true; });
        DeselectAllCommand = new RelayCommand(() => { foreach (var f in Files) f.Selected = false; });

        _downloader.ProgressChanged += p =>
        {
            CurrentFile = p.CurrentFile;
            FileProgress = p.FileProgress;
            OverallProgress = p.OverallProgress;
            DownloadedFiles = p.DownloadedFiles;
            TotalFiles = p.TotalFiles;
        };
        _downloader.StatusChanged += s => Status = s;
        _downloader.Completed += (ok, msg) =>
        {
            IsDownloading = false;
            if (ok) ResultPath = msg;
            Status = ok ? "Download complete!" : $"Error: {msg}";
        };
    }

    // ── Properties ──────────────────────────────────

    public string RepoId { get; set; }
    public string DownloadPath { get; set; }
    public ObservableCollection<HfFile> Files { get; } = new();

    private string _status = "Ready";
    public string Status { get => _status; set { _status = value; OnPropertyChanged(); } }

    private string _currentFile = "";
    public string CurrentFile { get => _currentFile; set { _currentFile = value; OnPropertyChanged(); } }

    private double _fileProgress;
    public double FileProgress { get => _fileProgress; set { _fileProgress = value; OnPropertyChanged(); } }

    private double _overallProgress;
    public double OverallProgress { get => _overallProgress; set { _overallProgress = value; OnPropertyChanged(); } }

    private int _downloadedFiles;
    public int DownloadedFiles { get => _downloadedFiles; set { _downloadedFiles = value; OnPropertyChanged(); } }

    private int _totalFiles;
    public int TotalFiles { get => _totalFiles; set { _totalFiles = value; OnPropertyChanged(); } }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        set { _isDownloading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotDownloading)); }
    }
    public bool IsNotDownloading => !IsDownloading;

    private string? _resultPath;
    public string? ResultPath { get => _resultPath; set { _resultPath = value; OnPropertyChanged(); } }

    public bool WasDownloaded => _resultPath is not null;

    // ── Commands ────────────────────────────────────

    public ICommand FetchFilesCommand { get; }
    public ICommand DownloadSelectedCommand { get; }
    public ICommand DownloadAllCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }

    // ── Actions ─────────────────────────────────────

    private async Task FetchFiles()
    {
        Files.Clear();
        var (repoId, subfolder) = ParseRepoUrl(RepoId);
        var displayId = subfolder is not null ? $"{repoId}/{subfolder}" : repoId;
        Status = $"Fetching {displayId}...";
        try
        {
            var list = await _downloader.FetchRepoFiles(repoId, subfolder);
            foreach (var f in list) Files.Add(f);
            Status = $"Found {Files.Count} files in {displayId}";

            // Auto-append subfolder to download path
            if (subfolder is not null && !DownloadPath.EndsWith(subfolder))
                DownloadPath = Path.Combine(DownloadPath, subfolder);
            OnPropertyChanged(nameof(DownloadPath));
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Extract (owner/repo, subfolder) from URL.
    /// Handles: https://huggingface.co/DimQ1/nemotron-speech-onnx/tree/main/cpu
    ///          DimQ1/nemotron-speech-onnx/cpu
    ///          DimQ1/nemotron-speech-onnx
    /// </summary>
    private static (string repoId, string? subfolder) ParseRepoUrl(string input)
    {
        var s = input.Trim().TrimEnd('/');

        // Strip protocol + domain
        if (s.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase))
            s = s["https://huggingface.co/".Length..];
        else if (s.StartsWith("huggingface.co/", StringComparison.OrdinalIgnoreCase))
            s = s["huggingface.co/".Length..];

        // Split into owner/repo and optional /tree/main/subfolder
        var parts = s.Split('/');
        // parts[0] = owner, parts[1] = repo, parts[2..] = "tree", "main", "subfolder"...
        if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0)
        {
            var repoId = $"{parts[0]}/{parts[1]}";

            string? subfolder = null;
            if (parts.Length > 2)
            {
                // Skip "tree"/"main" or "resolve"/"main" segments
                var skip = 0;
                if (parts.Length > skip + 2 && parts[skip + 2].Equals("tree", StringComparison.OrdinalIgnoreCase))
                    skip += 2; // skip "tree/main"
                else if (parts.Length > skip + 2 && parts[skip + 2].Equals("resolve", StringComparison.OrdinalIgnoreCase))
                    skip += 2; // skip "resolve/main"

                var remaining = parts.Skip(2 + skip).ToArray();
                if (remaining.Length > 0)
                    subfolder = string.Join("/", remaining);
            }

            return (repoId, subfolder);
        }

        return (s, null);
    }

    private async Task DownloadSelected()
    {
        IsDownloading = true;
        Status = "Starting download...";
        var (repoId, _) = ParseRepoUrl(RepoId);
        await _downloader.DownloadFromHuggingFace(repoId, Files.ToList(), DownloadPath);
    }

    private async Task DownloadAll()
    {
        foreach (var f in Files) f.Selected = true;
        await DownloadSelected();
    }

    public void Dispose() => _downloader.Dispose();

    // ── INotifyPropertyChanged ──────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
