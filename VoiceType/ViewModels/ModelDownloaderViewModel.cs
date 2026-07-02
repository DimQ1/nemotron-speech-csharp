using System.Collections.ObjectModel;
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

public sealed class ModelDownloaderViewModel : INotifyPropertyChanged, IDisposable
{
    private const string DefaultRepoId = "DimQ1/nemotron-3.5-asr-streaming-0.6b-onnx-int8-cpu";

    private readonly ModelDownloaderService _customDownloader = new();
    private readonly Dictionary<AvailableModel, CancellationTokenSource> _activeCts = new();
    private string _modelsRootPath = string.Empty;
    private string _repoId = string.Empty;
    private string _loadedFoldersRepoId = string.Empty;
    private string _selectedFoldersRepoId = string.Empty;
    private HashSet<string> _selectedFolderKeys = [];
    private string? _resultModelPath;

    public ModelDownloaderViewModel()
    {
        var settings = SettingsService.Load();
        RepoId = ResolveDownloaderRepoId(settings);
        ModelsRootPath = ResolveDownloaderModelsRootPath(settings);
        _selectedFoldersRepoId = ResolveDownloaderSelectedFoldersRepoId(settings);
        _selectedFolderKeys = ResolveDownloaderSelectedFolders(settings);

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
        SelectAllCommand = new RelayCommand(SelectAllFolders);
        DeselectAllCommand = new RelayCommand(DeselectAllFolders);
        BrowseRootCommand = new RelayCommand(BrowseRoot);

        _customDownloader.ProgressChanged += p =>
        {
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                CurrentFile = p.CurrentFile; FileProgress = p.FileProgress;
                OverallProgress = p.OverallProgress; DownloadedFiles = p.DownloadedFiles; TotalFiles = p.TotalFiles;
            }));
        };
        _customDownloader.StatusChanged += s =>
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => StatusCustom = s));
        _customDownloader.Completed += (ok, msg) =>
        {
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                IsDownloading = false;
                if (ok)
                {
                    ResultPath = ModelsRootPath;
                    ResultModelPath = _resultModelPath;
                }
                else
                {
                    ResultPath = null;
                    ResultModelPath = null;
                }
                StatusCustom = ok ? "Download complete!" : $"Error: {msg}";
            }));
        };
    }

    // ── Predefined models (independent per-model download) ──
    public ObservableCollection<AvailableModel> AvailableModels { get; }

    private async Task DownloadModel(AvailableModel? model)
    {
        if (model is null || model.IsDownloading) return;
        ResultPath = null;
        ResultModelPath = null;
        model.IsDownloading = true;
        model.Progress = 0;
        model.StatusMessage = "Fetching file list...";

        using var downloader = new ModelDownloaderService();
        var cts = new CancellationTokenSource();
        _activeCts[model] = cts;

        downloader.StatusChanged += s =>
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => model.StatusMessage = s));
        downloader.ProgressChanged += p =>
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => model.Progress = p.OverallProgress));

        try
        {
            await downloader.DownloadModelRepo(model.RepoId, model.SubfolderName, ModelsRootPath, cts.Token);
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                model.IsDownloading = false;
                model.IsDownloaded = true;
                model.Progress = 100;
                model.StatusMessage = "✓ Downloaded";
                ResultPath = ModelsRootPath;
                ResultModelPath = GetInstalledModelPath(ModelsRootPath, model.SubfolderName);
            }));
        }
        catch (OperationCanceledException)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                model.IsDownloading = false;
                model.StatusMessage = "Paused — click to resume";
            }));
        }
        catch (Exception ex)
        {
            _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                model.IsDownloading = false;
                model.StatusMessage = $"✗ {ex.Message}";
            }));
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
    public string RepoId
    {
        get => _repoId;
        set
        {
            if (string.Equals(_repoId, value, StringComparison.Ordinal))
                return;

            _repoId = value;
            OnPropertyChanged();
        }
    }
    public string ModelsRootPath
    {
        get => _modelsRootPath;
        set
        {
            if (string.Equals(_modelsRootPath, value, StringComparison.Ordinal))
                return;

            _modelsRootPath = value;
            OnPropertyChanged();
        }
    }
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
    public string? ResultModelPath { get => _resultModelPath; private set { _resultModelPath = value; OnPropertyChanged(); } }
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
        var repoId = ParseRepoId(RepoId);
        var selectionToApply = GetSelectionToApply(repoId);

        Folders.Clear();
        StatusCustom = $"Fetching {repoId}...";
        try
        {
            var list = await _customDownloader.FetchRepoFolders(repoId);
            if (selectionToApply is not null)
                ApplySelectedFolders(list, selectionToApply);

            foreach (var folder in list)
                Folders.Add(folder);

            RememberFolderSelection(repoId, Folders);
            _loadedFoldersRepoId = repoId;
            StatusCustom = $"Found {Folders.Count} folder(s) in {repoId}";
        }
        catch (Exception ex) { StatusCustom = $"Error: {ex.Message}"; }
    }

    private async Task DownloadCustom()
    {
        ResultPath = null;
        ResultModelPath = null;
        IsDownloading = true; StatusCustom = "Starting download...";
        var repoId = ParseRepoId(RepoId);
        var selectedFolders = Folders.ToList();
        _resultModelPath = TryResolveCustomResultModelPath(selectedFolders, repoId, ModelsRootPath);
        await _customDownloader.DownloadFromHuggingFace(repoId, selectedFolders, ModelsRootPath);
    }

    public static string GetInstalledModelPath(string modelsRootPath, string subfolder) =>
        Path.Combine(modelsRootPath, subfolder);

    public static string? TryResolveCustomResultModelPath(IReadOnlyList<HfFolder> folders, string repoId, string modelsRootPath)
    {
        var selectedFolders = folders.Where(f => f.Selected).ToList();
        if (selectedFolders.Count != 1)
            return null;

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

    public void Dispose()
    {
        SaveDownloaderSettings();
        _customDownloader.Dispose();
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private void BrowseRoot()
    {
        var owner = Application.Current.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive)
            ?? Application.Current.MainWindow;
        var hwnd = owner is not null
            ? new WindowInteropHelper(owner).Handle
            : IntPtr.Zero;
        var path = FolderBrowser.Show("Select root folder for downloaded models",
            Directory.Exists(ModelsRootPath) ? ModelsRootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            hwnd);
        if (path is not null)
            ModelsRootPath = path;
    }

    public static string ResolveDownloaderRepoId(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DownloaderRepoId)
            ? DefaultRepoId
            : settings.DownloaderRepoId;

    public static string ResolveDownloaderModelsRootPath(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.DownloaderModelsRootPath))
            return settings.DownloaderModelsRootPath;

        if (!string.IsNullOrWhiteSpace(settings.ModelsRootPath))
            return settings.ModelsRootPath;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VoiceType", "Models");
    }

    public static string ResolveDownloaderSelectedFoldersRepoId(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.DownloaderSelectedFoldersRepoId)
            ? string.Empty
            : ParseRepoId(settings.DownloaderSelectedFoldersRepoId);

    public static HashSet<string> ResolveDownloaderSelectedFolders(AppSettings settings) =>
        settings.DownloaderSelectedFolders
            .Where(folder => folder is not null)
            .Select(folder => folder.Trim())
            .Where(folder => folder.Length > 0 || folder == string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void PersistDownloaderSettings(AppSettings settings, string repoId, string modelsRootPath)
    {
        settings.DownloaderRepoId = repoId.Trim();
        settings.DownloaderModelsRootPath = modelsRootPath.Trim();
    }

    public static void PersistDownloaderFolderSelection(AppSettings settings, string repoId, IEnumerable<string> selectedFolderKeys)
    {
        settings.DownloaderSelectedFoldersRepoId = ParseRepoId(repoId);
        settings.DownloaderSelectedFolders = selectedFolderKeys
            .Select(folder => folder.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static HashSet<string> CaptureSelectedFolderKeys(IEnumerable<HfFolder> folders) =>
        folders.Where(folder => folder.Selected)
            .Select(folder => folder.SubfolderName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static void ApplySelectedFolders(IEnumerable<HfFolder> folders, ISet<string> selectedFolderKeys)
    {
        foreach (var folder in folders)
            folder.Selected = selectedFolderKeys.Contains(folder.SubfolderName);
    }

    private void SaveDownloaderSettings()
    {
        var settings = SettingsService.Load();
        PersistDownloaderSettings(settings, RepoId, ModelsRootPath);

        if (Folders.Count > 0 && !string.IsNullOrWhiteSpace(_loadedFoldersRepoId))
            RememberFolderSelection(_loadedFoldersRepoId, Folders);

        PersistDownloaderFolderSelection(settings, _selectedFoldersRepoId, _selectedFolderKeys);
        SettingsService.Save(settings);
    }

    private HashSet<string>? GetSelectionToApply(string repoId)
    {
        if (Folders.Count > 0 && string.Equals(_loadedFoldersRepoId, repoId, StringComparison.OrdinalIgnoreCase))
        {
            RememberFolderSelection(repoId, Folders);
            return new HashSet<string>(_selectedFolderKeys, StringComparer.OrdinalIgnoreCase);
        }

        if (string.Equals(_selectedFoldersRepoId, repoId, StringComparison.OrdinalIgnoreCase))
            return new HashSet<string>(_selectedFolderKeys, StringComparer.OrdinalIgnoreCase);

        return null;
    }

    private void RememberFolderSelection(string repoId, IEnumerable<HfFolder> folders)
    {
        _selectedFoldersRepoId = repoId;
        _selectedFolderKeys = CaptureSelectedFolderKeys(folders);
    }

    private void SelectAllFolders()
    {
        foreach (var folder in Folders)
            folder.Selected = true;

        RememberFolderSelection(ParseRepoId(RepoId), Folders);
    }

    private void DeselectAllFolders()
    {
        foreach (var folder in Folders)
            folder.Selected = false;

        RememberFolderSelection(ParseRepoId(RepoId), Folders);
    }
}