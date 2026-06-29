using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using VoiceType.Services;

namespace VoiceType.ViewModels;

public sealed class ModelDownloaderViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ModelDownloaderService _downloader = new();

    public ModelDownloaderViewModel()
    {
        RepoId = "DimQ1/nemotron-speech-onnx";
        DownloadPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceType", "Models", "nemotron-speech-onnx");

        FetchFilesCommand = new AsyncRelayCommand(FetchFolders);
        DownloadCommand = new AsyncRelayCommand(Download,
            () => Folders.Count > 0 && !IsDownloading);
        CancelCommand = new RelayCommand(() => _downloader.Cancel(), () => IsDownloading);
        SelectAllCommand = new RelayCommand(() => { foreach (var f in Folders) f.Selected = true; });
        DeselectAllCommand = new RelayCommand(() => { foreach (var f in Folders) f.Selected = false; });

        _downloader.ProgressChanged += p =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentFile = p.CurrentFile; FileProgress = p.FileProgress;
                OverallProgress = p.OverallProgress; DownloadedFiles = p.DownloadedFiles; TotalFiles = p.TotalFiles;
            });
        };
        _downloader.StatusChanged += s => Application.Current.Dispatcher.Invoke(() => Status = s);
        _downloader.Completed += (ok, msg) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsDownloading = false;
                if (ok) ResultPath = msg;
                Status = ok ? "Download complete!" : $"Error: {msg}";
            });
        };
    }

    public string RepoId { get; set; }
    public string DownloadPath { get; set; }
    public ObservableCollection<HfFolder> Folders { get; } = new();
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
    public bool IsDownloading { get => _isDownloading; set { _isDownloading = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsNotDownloading)); } }
    public bool IsNotDownloading => !IsDownloading;
    private string? _resultPath;
    public string? ResultPath { get => _resultPath; set { _resultPath = value; OnPropertyChanged(); } }
    public bool WasDownloaded => _resultPath is not null;

    public ICommand FetchFilesCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }

    private async Task FetchFolders()
    {
        Folders.Clear();
        var repoId = ParseRepoId(RepoId);
        Status = $"Fetching {repoId}...";
        try { var list = await _downloader.FetchRepoFolders(repoId); foreach (var f in list) Folders.Add(f); Status = $"Found {Folders.Count} folder(s) in {repoId}"; }
        catch (Exception ex) { Status = $"Error: {ex.Message}"; }
    }

    private async Task Download()
    {
        IsDownloading = true; Status = "Starting download...";
        var repoId = ParseRepoId(RepoId);
        await _downloader.DownloadFromHuggingFace(repoId, Folders.ToList(), DownloadPath);
    }

    private static string ParseRepoId(string input)
    {
        var s = input.Trim().TrimEnd('/');
        if (s.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase)) s = s["https://huggingface.co/".Length..];
        else if (s.StartsWith("huggingface.co/", StringComparison.OrdinalIgnoreCase)) s = s["huggingface.co/".Length..];
        var parts = s.Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : s;
    }

    public void Dispose() => _downloader.Dispose();
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}