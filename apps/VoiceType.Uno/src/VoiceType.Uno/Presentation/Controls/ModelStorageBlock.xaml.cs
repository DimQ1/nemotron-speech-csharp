using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.Controls;

/// <summary>
/// The one "Model storage" block: root folder, a live line per kind of model (<c>asr/</c>, <c>llm/</c>, <c>diarization/</c>) with
/// count and size, total and free space, "Open folder" and "Change folder…". All text comes from <see cref="ModelStorageText"/>
/// and <see cref="ModelStorageScanner"/>; this class only measures off the UI thread and fills the labels.
/// </summary>
public sealed partial class ModelStorageBlock : UserControl
{
    private int _version;

    public ModelStorageBlock()
    {
        InitializeComponent();
        TitleText.Text = ModelStorageText.Title;
        ExplanationText.Text = ModelStorageText.Explanation;
        RootCaption.Text = ModelStorageText.RootLabel;
        OpenFolderLabel.Text = ModelStorageText.OpenFolder;
        RootText.FontFamily = VoiceType.Uno.Presentation.SettingsUi.SettingsRows.MonoFont;
        RestartLabel.Text = StorageFolderText.RestartNow;
        DontMoveLabel.Text = StorageFolderText.DontMove;

        // Phones keep models in the app's private storage: there is no file manager to open and no path to pick.
        var desktop = !VoiceType.Uno.Presentation.SettingsUi.PlatformUi.IsAndroidLayout;
        ButtonRow.Visibility = desktop ? Visibility.Visible : Visibility.Collapsed;
        Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>Raised after the user changed the storage folder here (pages refresh their cards).</summary>
    public event Action? StorageChanged;

    /// <summary>Measures the folders off the UI thread and fills the labels; later calls win over earlier ones.</summary>
    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _version);
        var settings = App.Services.GetRequiredService<SettingsStore>().Current;
        var layout = ModelStorageLayout.Resolve(settings);
        var note = !string.IsNullOrWhiteSpace(settings.PendingModelsRoot)
            ? ModelStorageText.PendingMoveNote(settings.PendingModelsRoot)
            : settings.ModelStorageNote;

        RootText.Text = layout.Root;
        BadgeText.Text = StorageFolderText.Badge(layout.Root, AppPaths.ModelsDir);
        // A pending move offers "Restart now" (desktop only: Android never has one) and "Don't move".
        PendingButtons.Visibility = !string.IsNullOrWhiteSpace(settings.PendingModelsRoot) ? Visibility.Visible : Visibility.Collapsed;
        RestartButton.Visibility = VoiceType.Uno.Presentation.SettingsUi.AppRestart.IsAvailable ? Visibility.Visible : Visibility.Collapsed;
        var summary = await Task.Run(() => ModelStorageScanner.Summarize(layout, note));
        if (version != Volatile.Read(ref _version))
            return;

        SpeechLine.Text = summary.Line(ModelKind.Speech).Text;
        LanguageLine.Text = summary.Line(ModelKind.Language).Text;
        SpeakerLine.Text = summary.Line(ModelKind.Speaker).Text;
        TotalLine.Text = summary.TotalText;
        NoteText.Text = summary.Note;
        NoteText.Visibility = string.IsNullOrEmpty(summary.Note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Restart_Click(object sender, RoutedEventArgs e) => VoiceType.Uno.Presentation.SettingsUi.AppRestart.RestartNow();

    /// <summary>"Don't move": forgets the pending move; the models stay where they are.</summary>
    private async void DontMove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var main = App.Services.GetRequiredService<MainViewModel>();
            var built = main.CreateSettingsSnapshot();
            built.PendingModelsRoot = "";
            await main.ApplySettingsAsync(built);
            await RefreshAsync();
            StorageChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Could not drop the pending models move: {ex}");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var layout = ModelStorageLayout.Current;
        try
        {
            layout.EnsureDirectories();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Opening the root still works without the sub-folders.
        }
        FolderOpener.Open(layout.Root);
    }

    private async void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (await ModelStorageUi.ChangeFolderAsync(XamlRoot))
            {
                await RefreshAsync();
                StorageChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Model storage folder change failed: {ex}");
        }
    }
}
