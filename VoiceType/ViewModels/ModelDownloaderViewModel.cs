using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using VoiceType.Models;
using VoiceType.Services;

namespace VoiceType.ViewModels;

public sealed class ModelDownloaderViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ModelDownloaderService _customDownloader = new();
    private readonly Dictionary<AvailableModel, CancellationTokenSource> _activeCts = new();

    public ModelDownloaderViewModel()
    {
        RepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu";
        ModelsRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceType", "Models");

        AvailableModels = new ObservableCollection<AvailableModel>(
            AvailableModel.CpuModels.Select(m =>
            {
                var copy = new AvailableModel
                {
                    Name = m.Name, RepoId = m.RepoId, Description = m.Description,
                    SizeDisplay = m.SizeDisplay, Precision = m.Precision
                };
                copy.IsDownloaded = Directory.Exists(Path.Combine(ModelsRootPath, copy.SubfolderName))
                    && File.Exists(Path.Combine(ModelsRootPath, copy.SubfolderName, "genai_config.json"));
                return copy;
            }));

        DownloadModelCommand = new AsyncRelayCommand<AvailableModel>(DownloadModel,
            m => m is not null && m.CanDownload);
        CancelModelCommand = new RelayCommand<AvailableModel>(CancelModel,
            m => m is not null && m.IsDownloading);

        // Custom repo commands
        FetchFilesCommand = new AsyncRelayCommand(FetchFolders);
        DownloadCommand = new AsyncRelayCommand(DownloadCustom,
            () => Folders.Count > 0 && !IsDownloading);
        CancelCommand = new RelayCommand(() => _customDownloader.Cancel(), () => IsDownloading);
        SelectAllCommand = new RelayCommand(() => { foreach (var f in Folders) f.Selected = true; });
        DeselectAllCommand = new RelayCommand(() => { foreach (var f in Folders) f.Selected = false; });
        BrowseRootCommand = new RelayCommand(BrowseRoot);

        _customDownloader.ProgressChanged += p =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                CurrentFile = p.CurrentFile; FileProgress = p.FileProgress;
                OverallProgress = p.OverallProgress; DownloadedFiles = p.DownloadedFiles; TotalFiles = p.TotalFiles;
            });
        };
        _customDownloader.StatusChanged += s => Application.Current.Dispatcher.Invoke(() => StatusCustom = s);
        _customDownloader.Completed += (ok, msg) =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsDownloading = false;
                if (ok) ResultPath = msg;
                StatusCustom = ok ? "Download complete!" : $"Error: {msg}";
            });
        };
    }

    // ── Predefined models (independent per-model download) ──
    public ObservableCollection<AvailableModel> AvailableModels { get; }

    private async Task DownloadModel(AvailableModel? model)
    {
        if (model is null || model.IsDownloading) return;
        model.IsDownloading = true;
        model.Progress = 0;
        model.StatusMessage = "Fetching file list...";

        using var downloader = new ModelDownloaderService();
        var cts = new CancellationTokenSource();
        _activeCts[model] = cts;

        downloader.StatusChanged += s =>
            Application.Current.Dispatcher.Invoke(() => model.StatusMessage = s);
        downloader.ProgressChanged += p =>
            Application.Current.Dispatcher.Invoke(() => model.Progress = p.OverallProgress);

        try
        {
            await downloader.DownloadModelRepo(model.RepoId, model.SubfolderName, ModelsRootPath, cts.Token);
            Application.Current.Dispatcher.Invoke(() =>
            {
                model.IsDownloading = false;
                model.IsDownloaded = true;
                model.Progress = 100;
                model.StatusMessage = "✓ Downloaded";
                if (string.IsNullOrEmpty(ResultPath))
                    ResultPath = ModelsRootPath;
            });
        }
        catch (OperationCanceledException)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                model.IsDownloading = false;
                model.StatusMessage = "Paused — click to resume";
            });
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                model.IsDownloading = false;
                model.StatusMessage = $"✗ {ex.Message}";
            });
        }
        finally
        {
            _activeCts.Remove(model);
            cts.Dispose();
        }
    }

    private void CancelModel(AvailableModel? model)
    {
        if (model is not null && _activeCts.TryGetValue(model, out var cts))
            cts.Cancel();
    }

    // ── Custom repo ────────────────────────────────
    public string RepoId { get; set; }
    public string ModelsRootPath { get; set; }
    public ObservableCollection<HfFolder> Folders { get; } = new();
    private string _statusCustom = "Ready";
    public string StatusCustom { get => _statusCustom; set { _statusCustom = value; OnPropertyChanged(); } }
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

    public ICommand DownloadModelCommand { get; }
    public ICommand CancelModelCommand { get; }
    public ICommand FetchFilesCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand BrowseRootCommand { get; }

    private async Task FetchFolders()
    {
        Folders.Clear();
        var repoId = ParseRepoId(RepoId);
        StatusCustom = $"Fetching {repoId}...";
        try { var list = await _customDownloader.FetchRepoFolders(repoId); foreach (var f in list) Folders.Add(f); StatusCustom = $"Found {Folders.Count} folder(s) in {repoId}"; }
        catch (Exception ex) { StatusCustom = $"Error: {ex.Message}"; }
    }

    private async Task DownloadCustom()
    {
        IsDownloading = true; StatusCustom = "Starting download...";
        var repoId = ParseRepoId(RepoId);
        await _customDownloader.DownloadFromHuggingFace(repoId, Folders.ToList(), ModelsRootPath);
    }

    public static string ParseRepoId(string input)
    {
        var s = input.Trim().TrimEnd('/');
        if (s.StartsWith("https://huggingface.co/", StringComparison.OrdinalIgnoreCase)) s = s["https://huggingface.co/".Length..];
        else if (s.StartsWith("huggingface.co/", StringComparison.OrdinalIgnoreCase)) s = s["huggingface.co/".Length..];
        var parts = s.Split('/');
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : s;
    }

    public void Dispose() => _customDownloader.Dispose();
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private void BrowseRoot()
    {
        var hwnd = new WindowInteropHelper(Application.Current.MainWindow).Handle;
        var path = FolderBrowser.Show("Select root folder for downloaded models",
            Directory.Exists(ModelsRootPath) ? ModelsRootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            hwnd);
        if (path is not null)
            ModelsRootPath = path;
    }
}