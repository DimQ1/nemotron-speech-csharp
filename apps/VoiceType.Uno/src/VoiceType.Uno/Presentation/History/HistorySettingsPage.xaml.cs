using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using VoiceType.Uno.History;
using VirtualKey = Windows.System.VirtualKey;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Settings › History and storage. Opened with <c>Frame.Navigate(typeof(HistorySettingsPage))</c>.
/// Each control writes its key at once through <see cref="VoiceType.Uno.Services.SettingsStore"/>.
/// </summary>
public sealed partial class HistorySettingsPage : Page
{
    private IReadOnlyList<HistoryUiOption<int>> _retention = HistoryUiStorage.RetentionChoices;
    private IReadOnlyList<HistoryUiOption<int>> _caps = HistoryUiStorage.StorageCapChoices;
    private readonly IReadOnlyList<HistoryUiOption<AudioQuality>> _qualities = HistoryUiStorage.QualityChoices;
    private bool _loading;
#if !VOICE_TYPE_WINDOWS
    private Windows.UI.Core.SystemNavigationManager? _systemNavigation;
#endif

    public HistorySettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadValues();
#if !VOICE_TYPE_WINDOWS
        try
        {
            _systemNavigation = Windows.UI.Core.SystemNavigationManager.GetForCurrentView();
            _systemNavigation.BackRequested += OnSystemBackRequested;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] System back is not available: {ex.Message}");
        }
#endif
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
#if !VOICE_TYPE_WINDOWS
        if (_systemNavigation is not null)
            _systemNavigation.BackRequested -= OnSystemBackRequested;
        _systemNavigation = null;
#endif
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        BackButton.Focus(FocusState.Programmatic);
        await RefreshUsageAsync();
    }

    private void LoadValues()
    {
        _loading = true;
        try
        {
            var h = HistoryServices.HistorySettings;
            EnabledSwitch.IsOn = h.Enabled;
            InjectSwitch.IsOn = h.SaveInjectDictations;
            ConfirmSwitch.IsOn = h.ConfirmDelete;
            JobsSwitch.IsOn = h.AllowJobsDuringRecording;
            TitlesSwitch.IsOn = h.AutoTitles;
            if (!VoiceType.Uno.Services.Jobs.GemmaAvailability.IsInstalled)
                TitlesDetail.Text = "Needs the Gemma translation model: download it on the Models page";
            InjectSwitch.IsEnabled = h.Enabled;
            if (OperatingSystem.IsAndroid())
                JobsDetail.Text = "Slower and uses more battery while you record";

            var retentionIndex = HistoryUiStorage.IndexOf(ref _retention, h.AudioRetentionDays, HistoryUiStorage.RetentionLabel);
            Fill(RetentionCombo, _retention.Select(c => c.Label), retentionIndex);
            var capIndex = HistoryUiStorage.IndexOf(ref _caps, h.MaxAudioStorageMb, HistoryUiStorage.StorageCapLabel);
            Fill(CapCombo, _caps.Select(c => c.Label), capIndex);
            var qualityIndex = Math.Max(0, _qualities.ToList().FindIndex(q => q.Value == h.AudioQuality));
            Fill(QualityCombo, _qualities.Select(c => c.Label), qualityIndex);
            QualityDetail.Text = _qualities[qualityIndex].Detail ?? "";
        }
        finally
        {
            _loading = false;
        }
    }

    private static void Fill(ComboBox combo, IEnumerable<string> labels, int selected)
    {
        combo.Items.Clear();
        foreach (var label in labels)
            combo.Items.Add(label);
        combo.SelectedIndex = selected;
    }

    private async Task RefreshUsageAsync()
    {
        var store = HistoryServices.Recordings;
        var chatsDir = HistoryServices.Chats.ChatsDir;
        var (usage, free) = await Task.Run(() =>
        {
            var measured = HistoryUiStorage.Measure(store.SessionsDir, chatsDir);
            long? freeBytes = null;
            try
            {
                var root = Path.GetPathRoot(Path.GetFullPath(store.SessionsDir));
                if (!string.IsNullOrEmpty(root))
                    freeBytes = new DriveInfo(root).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                // Free space is optional information.
            }
            return (measured, freeBytes);
        });
        var settings = HistoryServices.HistorySettings;
        UsageBar.Value = HistoryUiStorage.UsageFraction(usage, settings, free);
        UsageText.Text = HistoryUiStorage.UsageText(usage, settings, free);
        DeleteAllDetail.Text = HistoryUiFormat.Count(usage.Recordings, "recording") + " · " + HistoryUiFormat.Bytes(usage.TotalBytes)
            + " · always asks first";
        _usage = usage;
    }

    private HistoryStorageUsage _usage;

    private void Setting_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        bool enabled = EnabledSwitch.IsOn, inject = InjectSwitch.IsOn, confirm = ConfirmSwitch.IsOn, jobs = JobsSwitch.IsOn, titles = TitlesSwitch.IsOn;
        InjectSwitch.IsEnabled = enabled;
        HistoryServices.UpdateHistorySettings(h =>
        {
            h.AutoTitles = titles;
            h.Enabled = enabled;
            h.SaveInjectDictations = inject;
            h.ConfirmDelete = confirm;
            h.AllowJobsDuringRecording = jobs;
        });
    }

    private async void Retention_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RetentionCombo.SelectedIndex < 0)
            return;
        var days = _retention[RetentionCombo.SelectedIndex].Value;
        HistoryServices.UpdateHistorySettings(h => h.AudioRetentionDays = days);
        await RefreshUsageAsync();
    }

    private async void Cap_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CapCombo.SelectedIndex < 0)
            return;
        var mb = _caps[CapCombo.SelectedIndex].Value;
        HistoryServices.UpdateHistorySettings(h => h.MaxAudioStorageMb = mb);
        await RefreshUsageAsync();
    }

    private void Quality_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (QualityCombo.SelectedIndex < 0)
            return;
        var choice = _qualities[QualityCombo.SelectedIndex];
        QualityDetail.Text = choice.Detail ?? "";
        if (!_loading)
            HistoryServices.UpdateHistorySettings(h => h.AudioQuality = choice.Value);
    }

    private async void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        var store = HistoryServices.Recordings;
        var ids = await Task.Run(() => store.LoadAll().Recordings.Select(r => r.Id).ToList());
        if (ids.Count == 0)
        {
            await HistoryDialogs.ShowMessageAsync(XamlRoot, "History is empty", "There are no recordings to delete.");
            return;
        }
        // Always asks, without "Don't ask again" (design §4.6).
        var text = HistoryUiDeleteText.AllHistory(ids.Count, _usage.TotalBytes);
        if (!await HistoryDialogs.ConfirmDeleteAsync(XamlRoot, text, allowDontAsk: false))
            return;
        await Task.Run(() => HistoryServices.DeleteRecordingsNow(ids));
        await RefreshUsageAsync();
    }

    private void GoBack()
    {
        if (Frame?.CanGoBack == true)
            Frame.GoBack();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoBack();

    private void Page_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape || e.Key == VirtualKey.GoBack)
        {
            e.Handled = true;
            GoBack();
        }
    }

#if !VOICE_TYPE_WINDOWS
    private void OnSystemBackRequested(object? sender, Windows.UI.Core.BackRequestedEventArgs e)
    {
        if (e.Handled || Frame?.CanGoBack != true)
            return;
        e.Handled = true;
        Frame.GoBack();
    }
#endif
}
