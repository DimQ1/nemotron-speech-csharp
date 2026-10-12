using Microsoft.UI.Dispatching;
using SpeechLib.ModelDownload;
using VoiceType.Uno.History;
using VoiceType.Uno.Services.Jobs;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Translate tab of <see cref="RecordingPage"/> (design §4.3, §8). The page feeds it the loaded recording
/// (<see cref="LoadAsync"/>) and the player position (<see cref="OnPlayerPosition"/>); a tap on a row raises
/// <see cref="SeekRequested"/> and the page moves the player.
/// </summary>
public sealed partial class TranslateSection : UserControl
{
    public TranslateSection()
    {
        ViewModel = new TranslateViewModel(
            HistoryServices.Recordings,
            HistoryServices.Jobs,
            App.Services.GetRequiredService<JobRunner>(),
            App.Services.GetRequiredService<ModelDownloadManager>(),
            DispatcherQueue.GetForCurrentThread());
        InitializeComponent();
        Unloaded += (_, _) => ViewModel.Dispose();
        SizeChanged += (_, _) => LimitStateArea();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranslateViewModel.HasRows))
                LimitStateArea();
        };
    }

    /// <summary>
    /// The state cards (hint, progress, partial) scroll when the window is low: half of the tab with a transcript
    /// below them, all of it when there is none, so the list never shrinks to nothing and no button is cut off.
    /// </summary>
    private void LimitStateArea()
    {
        if (ActualHeight <= 0)
            return;
        StateScroll.MaxHeight = ViewModel.HasRows ? Math.Max(96, ActualHeight * 0.5) : ActualHeight;
        // Low landscape windows: the explanation goes, the title and the button stay.
        EmptyDescription.Visibility = ActualHeight < 260 ? Visibility.Collapsed : Visibility.Visible;
    }

    public TranslateViewModel ViewModel { get; }

    /// <summary>The user tapped a row: seek the player to this many seconds.</summary>
    public event Action<double>? SeekRequested;

    public Task LoadAsync(string recordingId, string? viewedVersion) => ViewModel.LoadAsync(recordingId, viewedVersion);

    /// <summary>Highlights (and, when following, scrolls to) the row at the player position.</summary>
    public void OnPlayerPosition(TimeSpan position)
    {
        var index = ViewModel.Highlight(position.TotalSeconds);
        if (index >= 0 && RowsList.ContainerFromIndex(index) is UIElement container)
        {
            try
            {
                container.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.3, AnimationDesired = true });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VoiceType.Uno] Follow scroll failed: {ex.Message}");
            }
        }
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TranslateRowViewModel row)
            return;
        SeekRequested?.Invoke(row.Start);
        ViewModel.Highlight(row.Start);
    }

    // ── Language picker ───────────────────────────────────────────────────

    private void Picker_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var item in ViewModel.Translations)
        {
            var id = item.Id;
            var entry = new ToggleMenuFlyoutItem
            {
                Text = item.Label + (item.IsPartial ? " · " + item.StatusText : ""),
                IsChecked = id == ViewModel.SelectedId,
                MinHeight = 44,
            };
            entry.Click += (_, _) => ViewModel.Select(id);
            menu.Items.Add(entry);
        }
        if (menu.Items.Count > 0)
            menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(LanguageItems("New language…"));
        menu.ShowAt((FrameworkElement)sender);
    }

    private MenuFlyoutSubItem LanguageItems(string text)
    {
        var sub = new MenuFlyoutSubItem { Text = text, MinHeight = 44, IsEnabled = ViewModel.CanTranslate };
        foreach (var language in ViewModel.Languages)
            sub.Items.Add(LanguageItem(language));
        return sub;
    }

    private MenuFlyoutItem LanguageItem(TranslateChoice language)
    {
        var item = new MenuFlyoutItem { Text = language.Name, MinHeight = 44 };
        item.Click += (_, _) => ViewModel.TryStart(language.Code);
        return item;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        foreach (var language in ViewModel.Languages)
            menu.Items.Add(LanguageItem(language));
        menu.ShowAt((FrameworkElement)sender);
    }

    private void Original_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowOriginal = !ViewModel.ShowOriginal;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(OriginalButton, ViewModel.ShowOriginalText);
    }

    // ── More ⋯ ────────────────────────────────────────────────────────────

    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var again = new MenuFlyoutItem { Text = "Translate again", Icon = new SymbolIcon(Symbol.Refresh), MinHeight = 44 };
        again.IsEnabled = ViewModel.Selected is not null && !ViewModel.IsBusy && ViewModel.CanTranslate;
        again.Click += async (_, _) => await TranslateAgainAsync();
        menu.Items.Add(again);
        var delete = new MenuFlyoutItem { Text = "Delete translation…", Icon = new SymbolIcon(Symbol.Delete), MinHeight = 44 };
        delete.Click += async (_, _) => await DeleteAsync();
        menu.Items.Add(delete);
        menu.ShowAt((FrameworkElement)sender);
    }

    private async Task TranslateAgainAsync()
    {
        if (ViewModel.Selected is not { } selected)
            return;
        var name = TranslateTabLogic.LanguageName(selected.TargetLanguage);
        if (!await HistoryDialogs.AskAsync(XamlRoot, "Translate again?",
                $"This replaces the {name} translation of {selected.SourceVersion}: it is translated again from the start.", "Translate again"))
            return;
        ViewModel.TranslateAgain(selected.SourceVersion, selected.TargetLanguage);
    }

    private async Task DeleteAsync()
    {
        if (ViewModel.Selected is not { } selected)
            return;
        var name = TranslateTabLogic.LanguageName(selected.TargetLanguage);
        var text = new HistoryUiConfirmation($"Delete the {name} translation?",
            $"The {name} translation of {selected.SourceVersion} is deleted. The transcript itself is not changed.");
        if (await HistoryDialogs.ConfirmDeleteAsync(XamlRoot, text, allowDontAsk: false))
            await ViewModel.DeleteSelectedAsync();
    }

    // ── State buttons ─────────────────────────────────────────────────────

    private void Continue_Click(object sender, RoutedEventArgs e) => ViewModel.Continue();

    private void Stop_Click(object sender, RoutedEventArgs e) => ViewModel.Stop();

    private void TranslateCurrent_Click(object sender, RoutedEventArgs e) => ViewModel.TranslateCurrentVersion();

    private void Download_Click(object sender, RoutedEventArgs e) => ViewModel.DownloadModel();
}
