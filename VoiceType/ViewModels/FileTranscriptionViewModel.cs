using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using VoiceType.Models;
using VoiceType.Services;

namespace VoiceType.ViewModels;

/// <summary>
/// ViewModel for transcribing existing audio files.
/// </summary>
public sealed class FileTranscriptionViewModel : INotifyPropertyChanged
{
    private readonly FileTranscriptionService _transcriptionService = new();
    private AppSettings _settings;
    private string _selectedFile = "";
    private string _statusText = "Choose a WAV or MP3 file.";
    private string _resultText = "";
    private bool _isBusy;
    private string _engineSummary = "";

    public FileTranscriptionViewModel()
    {
        _settings = SettingsService.Load();
        RefreshEngineSummary();

        BrowseFileCommand = new RelayCommand(BrowseFile, () => !IsBusy);
        TranscribeCommand = new AsyncRelayCommand(TranscribeAsync, () => !IsBusy && !string.IsNullOrEmpty(SelectedFile));
        CopyCommand = new RelayCommand(CopyText, () => !string.IsNullOrEmpty(ResultText));
    }

    public string SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (!SetProperty(ref _selectedFile, value))
                return;

            OnPropertyChanged(nameof(FileName));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string FileName => string.IsNullOrEmpty(SelectedFile) ? "No file selected" : Path.GetFileName(SelectedFile);

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ResultText
    {
        get => _resultText;
        set
        {
            if (!SetProperty(ref _resultText, value))
                return;

            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (!SetProperty(ref _isBusy, value))
                return;

            OnPropertyChanged(nameof(ActionButtonText));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string ActionButtonText => IsBusy ? "Recognizing..." : "Recognize File";

    public string EngineSummary
    {
        get => _engineSummary;
        private set => SetProperty(ref _engineSummary, value);
    }

    public ICommand BrowseFileCommand { get; }
    public ICommand TranscribeCommand { get; }
    public ICommand CopyCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void BrowseFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select audio file",
            Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3|WAV Files (*.wav)|*.wav|MP3 Files (*.mp3)|*.mp3|All Files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrEmpty(SelectedFile))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(SelectedFile);
            dialog.FileName = Path.GetFileName(SelectedFile);
        }

        if (dialog.ShowDialog() == true)
        {
            SelectedFile = dialog.FileName;
            StatusText = "Ready to recognize.";
        }
    }

    private async Task TranscribeAsync()
    {
        if (string.IsNullOrEmpty(SelectedFile))
            return;

        try
        {
            _settings = SettingsService.Load();
            RefreshEngineSummary();
            IsBusy = true;
            StatusText = "Recognizing audio file...";
            ResultText = "";

            var text = await Task.Run(() => _transcriptionService.Transcribe(SelectedFile, _settings));
            ResultText = text;
            StatusText = string.IsNullOrWhiteSpace(text)
                ? "Recognition finished, but no text was produced."
                : "Recognition completed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void CopyText()
    {
        if (!string.IsNullOrEmpty(ResultText))
            Clipboard.SetText(ResultText);
    }

    private void RefreshEngineSummary()
    {
        var model = string.IsNullOrEmpty(_settings.SelectedModel)
            ? (string.IsNullOrEmpty(_settings.ModelPath) ? "default model" : Path.GetFileName(_settings.ModelPath))
            : _settings.SelectedModel;
        EngineSummary = $"Model: {model} • EP: {_settings.ExecutionProvider} • Language: {_settings.Language}";
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
