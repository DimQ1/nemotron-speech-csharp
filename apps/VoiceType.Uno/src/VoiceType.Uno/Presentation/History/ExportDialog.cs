using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.History;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Export sheet (design §4.7, decision 7): <c>.txt</c> with timestamps (optionally with the
/// translation under each line) and "Audio for media players" (the stored audio + <c>.lrc</c>
/// + <c>.srt</c> with one base name). "Save to…" asks for a folder with the platform
/// picker; "Save to Documents" writes to a fixed folder and shows the path, for heads
/// where the picker is unavailable.
/// </summary>
internal sealed partial class ExportDialog : ContentDialog
{
    private readonly RecordingManifest _manifest;
    private readonly TranscriptDocument _transcript;
    private readonly TranslationDocument? _translation;
    private readonly string? _audioPath;
    private readonly long _audioBytes;
    private readonly CheckBox _text;
    private readonly ToggleSwitch _withTranslation;
    private readonly CheckBox _audio;
    private readonly TextBlock _summary;

    private ExportDialog(RecordingManifest manifest, string versionId, TranscriptDocument transcript,
        TranslationInfo? translationInfo, TranslationDocument? translation, string? audioPath, long audioBytes)
    {
        _manifest = manifest;
        _transcript = transcript;
        _translation = translation;
        _audioPath = audioPath;
        _audioBytes = audioBytes;

        Title = "Export";
        PrimaryButtonText = "Save to…";
        SecondaryButtonText = "Quick save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var panel = new StackPanel { Spacing = 8, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = manifest.EffectiveTitle + " · " + versionId,
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        });

        _text = Option("Transcript with timestamps (.txt)", "[00:03:20] text, one line per segment", isChecked: true);
        panel.Children.Add(_text);

        _withTranslation = new ToggleSwitch { OnContent = "", OffContent = "", MinWidth = 0, MinHeight = 44 };
        var translationRow = new Grid { ColumnSpacing = 12, MinHeight = 44 };
        translationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        translationRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var language = translationInfo?.TargetLanguage.ToUpperInvariant() ?? "";
        translationRow.Children.Add(Label($"Add the {language} translation", "Under each line of .txt and .srt"));
        Grid.SetColumn(_withTranslation, 1);
        translationRow.Children.Add(_withTranslation);
        AutomationProperties.SetName(_withTranslation, "Add the translation");
        translationRow.Visibility = translation is null ? Visibility.Collapsed : Visibility.Visible;
        panel.Children.Add(translationRow);

        var audioDetail = audioPath is null
            ? "No audio: it was not recorded or was removed by the retention rule"
            : $"{Path.GetExtension(audioPath).TrimStart('.').ToUpperInvariant()} audio ({HistoryUiFormat.Bytes(audioBytes)}) + .lrc (music players) + .srt (video players, VLC)";
        _audio = Option("Audio for media players", audioDetail, isChecked: audioPath is not null);
        _audio.IsEnabled = audioPath is not null;
        panel.Children.Add(_audio);

        _summary = new TextBlock { FontSize = 12, Opacity = 0.75, Margin = new Thickness(0, 4, 0, 0) };
        panel.Children.Add(_summary);
        panel.Children.Add(new TextBlock
        {
            Text = "Quick save writes to " + DocumentsFolder(),
            FontSize = 11,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
        });

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _text.Checked += (_, _) => Update();
        _text.Unchecked += (_, _) => Update();
        _audio.Checked += (_, _) => Update();
        _audio.Unchecked += (_, _) => Update();
        _withTranslation.Toggled += (_, _) => Update();
        Update();
    }

    /// <summary>Loads what the sheet needs off the UI thread; null when the recording is gone.</summary>
    public static async Task<ExportDialog?> CreateAsync(string recordingId, string? versionId)
    {
        var store = HistoryServices.Recordings;
        var data = await Task.Run(() =>
        {
            var entry = store.Get(recordingId);
            if (entry is null)
                return default;
            var version = versionId ?? entry.Manifest.CurrentVersion;
            if (version is null || entry.Manifest.FindVersion(version) is null)
                return default;
            var transcript = store.LoadTranscript(recordingId, version);
            var info = HistoryUiExport.PickTranslation(entry.Manifest, version);
            TranslationDocument? translation = null;
            try
            {
                if (info is not null)
                    translation = store.LoadTranslation(recordingId, info.Id);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                info = null;
            }
            var audio = store.GetAudioPath(recordingId);
            var bytes = audio is null ? 0 : new FileInfo(audio).Length;
            return (entry.Manifest, version, transcript, info, translation, audio, bytes);
        });
        if (data.Manifest is null)
            return null;
        return new ExportDialog(data.Manifest, data.version, data.transcript, data.info, data.translation, data.audio, data.bytes);
    }

    private IReadOnlyList<HistoryExportFile> Plan() => HistoryUiExport.Plan(
        _manifest, _transcript,
        new HistoryExportOptions(_text.IsChecked == true, _withTranslation.IsOn, _audio.IsChecked == true),
        _audioPath, _audioBytes, _translation);

    private void Update()
    {
        var files = Plan();
        _summary.Text = HistoryUiExport.Summary(files);
        IsPrimaryButtonEnabled = IsSecondaryButtonEnabled = files.Count > 0;
    }

    /// <summary>Shows the sheet and writes the files; reports the result in a message.</summary>
    public async Task RunAsync(XamlRoot? xamlRoot)
    {
        XamlRoot = xamlRoot;
        var result = await HistoryDialogs.ShowSafeAsync(this);
        if (result == ContentDialogResult.None)
            return;

        var files = Plan();
        try
        {
            string where;
            if (result == ContentDialogResult.Primary)
            {
                var folder = await PickFolderAsync(xamlRoot);
                if (folder is null)
                    return; // cancelled (or no picker on this head: "Save to Documents" remains)
                await WriteAsync(folder, files);
                where = folder.Path is { Length: > 0 } p ? p : folder.Name;
            }
            else
            {
                var dir = DocumentsFolder();
                await Task.Run(() => WriteToDirectory(dir, files));
                where = dir;
            }
            await HistoryDialogs.ShowMessageAsync(xamlRoot, "Exported",
                $"{HistoryUiFormat.Count(files.Count, "file")} saved to {where}:\n" + string.Join("\n", files.Select(f => f.FileName)));
        }
        catch (Exception ex)
        {
            await HistoryDialogs.ShowMessageAsync(xamlRoot, "Export failed", ex.Message);
        }
    }

    internal static async Task<StorageFolder?> PickFolderAsync(XamlRoot? xamlRoot)
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add("*");
#if VOICE_TYPE_WINDOWS
            // WinAppSDK pickers need the owner window handle.
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(xamlRoot!.ContentIslandEnvironment.AppWindowId);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
#endif
            return await picker.PickSingleFolderAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Folder picker unavailable: {ex.Message}");
            return null;
        }
    }

    internal static async Task WriteAsync(StorageFolder folder, IReadOnlyList<HistoryExportFile> files)
    {
        foreach (var file in files)
        {
            var target = await folder.CreateFileAsync(file.FileName, CreationCollisionOption.ReplaceExisting);
            await using var output = await target.OpenStreamForWriteAsync();
            output.SetLength(0);
            if (file.SourcePath is not null)
            {
                await using var input = File.OpenRead(file.SourcePath);
                await input.CopyToAsync(output);
            }
            else
            {
                var bytes = new System.Text.UTF8Encoding(false).GetBytes(file.Content ?? "");
                await output.WriteAsync(bytes);
            }
        }
    }

    private static void WriteToDirectory(string dir, IReadOnlyList<HistoryExportFile> files)
    {
        Directory.CreateDirectory(dir);
        foreach (var file in files)
        {
            var path = Path.Combine(dir, file.FileName);
            if (file.SourcePath is not null)
                File.Copy(file.SourcePath, path, overwrite: true);
            else
                ExportFiles.WriteText(path, file.Content ?? "");
        }
    }

    /// <summary>Documents/VoiceType on desktops; the app's external Documents folder on Android.</summary>
    internal static string DocumentsFolder()
    {
#if __ANDROID__
        var external = Android.App.Application.Context.GetExternalFilesDir(Android.OS.Environment.DirectoryDocuments)?.AbsolutePath;
        if (!string.IsNullOrEmpty(external))
            return Path.Combine(external, "VoiceType");
#endif
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents))
            documents = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents");
        return Path.Combine(documents, "VoiceType");
    }

    private static CheckBox Option(string title, string detail, bool isChecked)
    {
        var box = new CheckBox { Content = Label(title, detail), IsChecked = isChecked, MinHeight = 44 };
        AutomationProperties.SetName(box, title);
        return box;
    }

    private static StackPanel Label(string title, string detail)
    {
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        label.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        return label;
    }
}
