using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using SpeechLib.Models;
using VoiceType.Models;
using VoiceType.Services;

namespace VoiceType.ViewModels;

/// <summary>
/// ViewModel for the Batch Audio Transcription window.
/// Manages file list, batch settings, processing, audio playback, and transcript viewer.
/// </summary>
public sealed class BatchAudioViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly AudioPlaybackService _playback = new();
    private BatchTranscriptionService? _batchService;
    private CancellationTokenSource? _batchCts;
    private bool _isDisposed;

    public BatchAudioViewModel()
    {
        // ── Commands ──
        AddFilesCommand = new RelayCommand(AddFiles);
        AddFolderCommand = new RelayCommand(AddFolder);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedJob is not null);
        ClearAllCommand = new RelayCommand(ClearAll, () => Files.Count > 0);
        StartBatchCommand = new AsyncRelayCommand(StartBatchAsync, () => Files.Count > 0 && IsNotProcessing);
        CancelBatchCommand = new RelayCommand(CancelBatch, () => IsProcessing);
        OpenOutputDirCommand = new RelayCommand(OpenOutputDir);
        BrowseOutputDirCommand = new RelayCommand(BrowseOutputDir);
        BrowseDiarizationModelCommand = new RelayCommand(BrowseDiarizationModel);
        ViewFileCommand = new RelayCommand<AudioFileJob>(ViewFile);
        PlayPauseCommand = new RelayCommand(PlayPause, () => _playback.HasAudio);
        StopPlaybackCommand = new RelayCommand(StopPlayback, () => _playback.HasAudio);
        SeekToWordCommand = new RelayCommand<WordTiming>(SeekToWord);

        // ── Load persisted settings ──
        var settings = SettingsService.Load();
        ModelsRootPath = settings.ModelsRootPath;
        ExecutionProvider = settings.ExecutionProvider;
        Language = settings.Language;
        EnableDiarization = false;
        DiarizationModelPath = "";
        Parallelism = 2;
        OutputDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VoiceType", "Transcripts");
        ExportFormat = "txt";

        // ── Tab defaults ──
        SelectedTab = 0; // Plain Text

        // ── Scan available models ──
        ScanAsrModels();

        // ── Playback events ──
        _playback.PlaybackEnded += OnPlaybackEnded;
    }

    // ═══════════════════════════════════════════════════════════════
    // File List
    // ═══════════════════════════════════════════════════════════════

    public ObservableCollection<AudioFileJob> Files { get; } = new();

    public string FileCountSummary => Files.Count switch
    {
        0 => "No files added",
        1 => "1 file",
        _ => $"{Files.Count} files"
    };

    private AudioFileJob? _selectedJob;
    public AudioFileJob? SelectedJob
    {
        get => _selectedJob;
        set => SetProperty(ref _selectedJob, value);
    }

    // ═══════════════════════════════════════════════════════════════
    // Settings
    // ═══════════════════════════════════════════════════════════════

    private string _asrModelPath = "";
    public string AsrModelPath
    {
        get => _asrModelPath;
        set { SetProperty(ref _asrModelPath, value); }
    }

    private string _executionProvider = "cpu";
    public string ExecutionProvider
    {
        get => _executionProvider;
        set { SetProperty(ref _executionProvider, value); }
    }

    private string _language = "auto";
    public string Language
    {
        get => _language;
        set { SetProperty(ref _language, value); }
    }

    private bool _enableDiarization;
    public bool EnableDiarization
    {
        get => _enableDiarization;
        set { SetProperty(ref _enableDiarization, value); }
    }

    private string _diarizationModelPath = "";
    public string DiarizationModelPath
    {
        get => _diarizationModelPath;
        set { SetProperty(ref _diarizationModelPath, value); }
    }

    private int _parallelism = 2;
    public int Parallelism
    {
        get => _parallelism;
        set { SetProperty(ref _parallelism, Math.Max(1, Math.Min(8, value))); }
    }

    private string _outputDirectory = "";
    public string OutputDirectory
    {
        get => _outputDirectory;
        set { SetProperty(ref _outputDirectory, value); }
    }

    private string _exportFormat = "txt";
    public string ExportFormat
    {
        get => _exportFormat;
        set { SetProperty(ref _exportFormat, value); }
    }

    public ObservableCollection<string> AvailableAsrModels { get; } = new();

    private int _selectedAsrModelIndex = -1;
    public int SelectedAsrModelIndex
    {
        get => _selectedAsrModelIndex;
        set
        {
            if (SetProperty(ref _selectedAsrModelIndex, value) &&
                value >= 0 && value < AvailableAsrModels.Count)
            {
                AsrModelPath = Path.Combine(ModelsRootPath, AvailableAsrModels[value]);
            }
        }
    }

    private string _modelsRootPath = "";
    public string ModelsRootPath
    {
        get => _modelsRootPath;
        set { _modelsRootPath = value; ScanAsrModels(); }
    }

    // ═══════════════════════════════════════════════════════════════
    // Processing State
    // ═══════════════════════════════════════════════════════════════

    private bool _isProcessing;
    public bool IsProcessing
    {
        get => _isProcessing;
        set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                CancelVisible = value;
                OnPropertyChanged(nameof(IsNotProcessing));
                OnPropertyChanged(nameof(ProgressVisibility));
                OnPropertyChanged(nameof(ProgressSummary));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsNotProcessing => !_isProcessing;

    public Visibility ProgressVisibility => IsProcessing ? Visibility.Visible : Visibility.Collapsed;

    private string _progressSummary = "";
    public string ProgressSummary
    {
        get => _progressSummary;
        set { SetProperty(ref _progressSummary, value); }
    }

    private int _totalProgressPercent;
    public int TotalProgressPercent
    {
        get => _totalProgressPercent;
        set { SetProperty(ref _totalProgressPercent, value); }
    }

    // ═══════════════════════════════════════════════════════════════
    // Transcript Viewer
    // ═══════════════════════════════════════════════════════════════

    private int _selectedTab;
    public int SelectedTab
    {
        get => _selectedTab;
        set { SetProperty(ref _selectedTab, value); RefreshTranscriptView(); }
    }

    private string _plainTextView = "";
    public string PlainTextView
    {
        get => _plainTextView;
        set { SetProperty(ref _plainTextView, value); }
    }

    private string _wordTimingsView = "";
    public string WordTimingsView
    {
        get => _wordTimingsView;
        set { SetProperty(ref _wordTimingsView, value); }
    }

    private string _speakerView = "";
    public string SpeakerView
    {
        get => _speakerView;
        set { SetProperty(ref _speakerView, value); }
    }

    private bool _isViewerVisible;
    public bool IsViewerVisible
    {
        get => _isViewerVisible;
        set { SetProperty(ref _isViewerVisible, value); }
    }

    private string _viewerFileName = "";
    public string ViewerFileName
    {
        get => _viewerFileName;
        set { SetProperty(ref _viewerFileName, value); }
    }

    // ═══════════════════════════════════════════════════════════════
    // Playback
    // ═══════════════════════════════════════════════════════════════

    public bool IsPlaying => _playback.IsPlaying;
    public bool IsPaused => _playback.IsPaused;

    public string PlayPauseText => _playback.IsPlaying ? "⏸" : "▶";
    public string PlayPauseTooltip => _playback.IsPlaying ? "Pause" : "Play";

    private bool _cancelVisible;
    public bool CancelVisible
    {
        get => _cancelVisible;
        set { SetProperty(ref _cancelVisible, value); }
    }

    private TimeSpan _playbackPosition;
    public TimeSpan PlaybackPosition
    {
        get => _playbackPosition;
        set
        {
            if (SetProperty(ref _playbackPosition, value))
                OnPropertyChanged(nameof(PlaybackPositionText));
        }
    }

    public string PlaybackPositionText => FormatTimeSpan(_playbackPosition);

    private TimeSpan _playbackDuration;
    public TimeSpan PlaybackDuration
    {
        get => _playbackDuration;
        set
        {
            if (SetProperty(ref _playbackDuration, value))
                OnPropertyChanged(nameof(PlaybackDurationText));
        }
    }

    public string PlaybackDurationText => FormatTimeSpan(_playbackDuration);

    private double _playbackProgress; // 0.0 to 1.0
    public double PlaybackProgress
    {
        get => _playbackProgress;
        set { SetProperty(ref _playbackProgress, value); }
    }

    private int _highlightedWordIndex = -1;
    public int HighlightedWordIndex
    {
        get => _highlightedWordIndex;
        set { SetProperty(ref _highlightedWordIndex, value); }
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        var totalMinutes = (int)ts.TotalMinutes;
        return $"{totalMinutes}:{ts.Seconds:D2}.{ts.Milliseconds / 100}";
    }

    // ═══════════════════════════════════════════════════════════════
    // Commands
    // ═══════════════════════════════════════════════════════════════

    public ICommand AddFilesCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand RemoveSelectedCommand { get; }
    public ICommand ClearAllCommand { get; }
    public ICommand StartBatchCommand { get; }
    public ICommand CancelBatchCommand { get; }
    public ICommand OpenOutputDirCommand { get; }
    public ICommand BrowseOutputDirCommand { get; }
    public ICommand BrowseDiarizationModelCommand { get; }
    public ICommand ViewFileCommand { get; }
    public ICommand PlayPauseCommand { get; }
    public ICommand StopPlaybackCommand { get; }
    public ICommand SeekToWordCommand { get; }

    // ═══════════════════════════════════════════════════════════════
    // File Management
    // ═══════════════════════════════════════════════════════════════

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".m4a", ".ogg", ".wma", ".aiff", ".aif", ".aac"
    };

    private void NotifyFileListChanged()
    {
        OnPropertyChanged(nameof(FileCountSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    private void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Audio Files",
            Filter = "Audio files (*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.wma;*.aiff;*.aac)|*.wav;*.mp3;*.flac;*.m4a;*.ogg;*.wma;*.aiff;*.aac|All files (*.*)|*.*",
            Multiselect = true
        };

        var owner = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

        if (dlg.ShowDialog(owner) != true) return;

        foreach (var path in dlg.FileNames)
        {
            if (!Files.Any(f => f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                Files.Add(new AudioFileJob { FilePath = path });
        }

        NotifyFileListChanged();
    }

    private void AddFolder()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(
            Application.Current.MainWindow).Handle;
        var path = FolderBrowser.Show("Select folder with audio files", "", hwnd);
        if (path is null) return;

        foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)
            .Where(f => AudioExtensions.Contains(Path.GetExtension(f))))
        {
            if (!Files.Any(j => j.FilePath.Equals(file, StringComparison.OrdinalIgnoreCase)))
                Files.Add(new AudioFileJob { FilePath = file });
        }

        NotifyFileListChanged();
    }

    private void RemoveSelected()
    {
        if (SelectedJob is not null)
            Files.Remove(SelectedJob);
        NotifyFileListChanged();
    }

    private void ClearAll()
    {
        Files.Clear();
        NotifyFileListChanged();
    }

    // ═══════════════════════════════════════════════════════════════
    // Batch Processing
    // ═══════════════════════════════════════════════════════════════

    private async Task StartBatchAsync()
    {
        if (IsProcessing || Files.Count == 0) return;

        IsProcessing = true;
        TotalProgressPercent = 0;
        _batchCts = new CancellationTokenSource();

        var langId = string.Equals(Language, "auto", StringComparison.OrdinalIgnoreCase) ? null : Language;

        _batchService = new BatchTranscriptionService(
            AsrModelPath,
            ExecutionProvider,
            langId,
            useVad: true,
            EnableDiarization,
            string.IsNullOrEmpty(DiarizationModelPath) ? null : DiarizationModelPath,
            OutputDirectory,
            ExportFormat,
            Parallelism);

        var progress = new Progress<(AudioFileJob? job, int totalPct)>(OnJobProgress);

        try
        {
            await _batchService.ProcessAllAsync(Files, progress, _batchCts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ProgressSummary = $"Error: {ex.Message}";
        }
        finally
        {
            UpdateProgressSummary();
            TotalProgressPercent = 100;
            IsProcessing = false;
            _batchService?.Dispose();
            _batchService = null;
            _batchCts?.Dispose();
            _batchCts = null;
        }
    }

    private void CancelBatch()
    {
        _batchCts?.Cancel();
        ProgressSummary = "Cancelling...";
    }

    private void OnJobProgress((AudioFileJob? job, int totalPct) report)
    {
        if (report.totalPct >= 0)
            TotalProgressPercent = report.totalPct;

        Application.Current.Dispatcher.BeginInvoke(() => UpdateProgressSummary());
    }

    private void UpdateProgressSummary()
    {
        var done = Files.Count(f => f.Status == "Done");
        var processing = Files.Count(f => f.Status == "Processing");
        var error = Files.Count(f => f.Status == "Error");
        var queued = Files.Count(f => f.Status == "Queued");

        var parts = new List<string>();
        if (done > 0) parts.Add($"✅ {done} done");
        if (processing > 0) parts.Add($"🔄 {processing} processing");
        if (error > 0) parts.Add($"❌ {error} errors");
        if (queued > 0) parts.Add($"⏳ {queued} queued");

        ProgressSummary = string.Join("  ", parts);
    }

    // ═══════════════════════════════════════════════════════════════
    // Transcript Viewer
    // ═══════════════════════════════════════════════════════════════

    private void ViewFile(AudioFileJob? job)
    {
        if (job is null || job.Status != "Done") return;

        SelectedJob = job;
        ViewerFileName = job.FileName;
        IsViewerVisible = true;

        // Load audio for playback
        try
        {
            _playback.Open(job.FilePath);
            PlaybackDuration = _playback.Duration;
            PlaybackPosition = TimeSpan.Zero;
            PlaybackProgress = 0;
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsPaused));
            CommandManager.InvalidateRequerySuggested();
            CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            ViewerFileName = $"Cannot play: {ex.Message}";
        }

        RefreshTranscriptView();
    }

    private void RefreshTranscriptView()
    {
        var job = SelectedJob;
        if (job is null) return;

        switch (SelectedTab)
        {
            case 0: // Plain Text
                PlainTextView = job.DiarizedText ?? job.PlainText ?? "";
                break;

            case 1: // Word Timings
                WordTimingsView = BuildWordTimingsView(job);
                break;

            case 2: // Speakers
                SpeakerView = BuildSpeakerView(job);
                break;
        }
    }

    private static string BuildWordTimingsView(AudioFileJob job)
    {
        if (job.WordTimings is not { Count: > 0 })
            return "No word timings available.";

        return string.Join("\n",
            job.WordTimings.Select(w =>
                $"{DiarizedUtterance.FormatTimestamp(w.StartSeconds)} → {DiarizedUtterance.FormatTimestamp(w.EndSeconds)}  {w.Word}"));
    }

    private static string BuildSpeakerView(AudioFileJob job)
    {
        if (job.SpeakerUtterances is not { Count: > 0 })
            return "No speaker diarization data. Enable diarization to see speaker segments.";

        var lines = new List<string>();
        string? lastSpeaker = null;
        double blockStart = 0;
        var blockText = new System.Text.StringBuilder();

        foreach (var u in job.SpeakerUtterances)
        {
            if (u.SpeakerId != lastSpeaker)
            {
                // Flush previous block
                if (lastSpeaker is not null)
                {
                    lines.Add($"── {lastSpeaker} ({DiarizedUtterance.FormatTimestamp(blockStart)} → {DiarizedUtterance.FormatTimestamp(blockStart)}) ──");
                    lines.Add(blockText.ToString().Trim());
                    lines.Add("");
                }

                lastSpeaker = u.SpeakerId;
                blockStart = u.StartSeconds;
                blockText.Clear();
            }

            if (blockText.Length > 0) blockText.Append(' ');
            blockText.Append(u.Text);
        }

        // Flush last block
        if (lastSpeaker is not null)
        {
            lines.Add($"── {lastSpeaker} ({DiarizedUtterance.FormatTimestamp(blockStart)} → {DiarizedUtterance.FormatTimestamp(blockStart)}) ──");
            lines.Add(blockText.ToString().Trim());
        }

        return string.Join("\n", lines);
    }

    // ═══════════════════════════════════════════════════════════════
    // Playback
    // ═══════════════════════════════════════════════════════════════

    private void PlayPause()
    {
        if (_playback.IsPlaying)
        {
            _playback.Pause();
        }
        else
        {
            _playback.Play();
        }

        Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(IsPaused));
            OnPropertyChanged(nameof(PlayPauseText));
            OnPropertyChanged(nameof(PlayPauseTooltip));
        });
    }

    private void StopPlayback()
    {
        _playback.Stop();
        PlaybackPosition = TimeSpan.Zero;
        PlaybackProgress = 0;
        HighlightedWordIndex = -1;

        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(PlayPauseText));
        OnPropertyChanged(nameof(PlayPauseTooltip));
    }

    private void SeekToWord(WordTiming? word)
    {
        if (word is null || !_playback.HasAudio) return;

        _playback.Seek(TimeSpan.FromSeconds(word.StartSeconds));
        PlaybackPosition = TimeSpan.FromSeconds(word.StartSeconds);
        PlaybackProgress = _playback.Duration.TotalSeconds > 0
            ? word.StartSeconds / _playback.Duration.TotalSeconds
            : 0;
    }

    private void OnPlaybackEnded()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            PlaybackPosition = _playback.Duration;
            PlaybackProgress = 1.0;
            HighlightedWordIndex = -1;
            OnPropertyChanged(nameof(IsPlaying));
            OnPropertyChanged(nameof(PlayPauseText));
            OnPropertyChanged(nameof(PlayPauseTooltip));
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // Settings Browsing
    // ═══════════════════════════════════════════════════════════════

    private void OpenOutputDir()
    {
        if (Directory.Exists(OutputDirectory))
            System.Diagnostics.Process.Start("explorer.exe", OutputDirectory);
        else
            System.Diagnostics.Process.Start("explorer.exe",
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    }

    private void BrowseOutputDir()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(
            Application.Current.MainWindow).Handle;
        var path = FolderBrowser.Show("Select output folder",
            Directory.Exists(OutputDirectory) ? OutputDirectory : "", hwnd);
        if (path is not null)
            OutputDirectory = path;
    }

    private void BrowseDiarizationModel()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Sortformer ONNX model",
            Filter = "ONNX files (*.onnx)|*.onnx|All files (*.*)|*.*",
            DefaultExt = ".onnx"
        };

        var owner = Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;

        if (dlg.ShowDialog(owner) == true)
            DiarizationModelPath = dlg.FileName;
    }

    private void ScanAsrModels()
    {
        AvailableAsrModels.Clear();

        var root = string.IsNullOrEmpty(_modelsRootPath)
            ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "models-onnx")
            : _modelsRootPath;

        if (!Path.IsPathRooted(root))
            root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, root));

        _modelsRootPath = root;
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.GetDirectories(root))
        {
            var configPath = Path.Combine(dir, "genai_config.json");
            if (File.Exists(configPath))
                AvailableAsrModels.Add(Path.GetFileName(dir));
        }

        // Select first model if nothing selected yet
        if (_selectedAsrModelIndex < 0 && AvailableAsrModels.Count > 0)
            SelectedAsrModelIndex = 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // INotifyPropertyChanged
    // ═══════════════════════════════════════════════════════════════

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _batchCts?.Cancel();
        _batchCts?.Dispose();
        _batchService?.Dispose();
        _playback.Dispose();
    }
}
