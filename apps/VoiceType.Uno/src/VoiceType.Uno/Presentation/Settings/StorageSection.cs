using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.Controls;
using VoiceType.Uno.Presentation.History;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>
/// Storage: one place that shows everything the app keeps (summary bar with free space per drive) and lets each folder be
/// changed: Model storage (the existing block), Recordings and App data (design voicetype-uno-settings §4).
/// </summary>
internal sealed class StorageSection : SettingsSectionView
{
    private TextBlock? _totalText;
    private TextBlock? _freeText;
    private Grid? _bar;
    private ChipWrapPanel? _legend;
    private ModelStorageBlock? _models;
    private FolderCard? _recordings;
    private FolderCard? _data;
    private int _version;

    /// <summary>The last measured Storage line of the hub (shared with the page so the list shows it too).</summary>
    public static string? LastHubLine { get; private set; }

    public StorageSection(SettingsContext context) : base(context)
    {
    }

    protected override UIElement Build()
    {
        var stack = new StackPanel();

        // ── Summary ───────────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader("Used by VoiceType"));
        _totalText = new TextBlock { Text = "Measuring…", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        _freeText = SettingsRows.Secondary("");
        _freeText.HorizontalAlignment = HorizontalAlignment.Right;
        _freeText.TextAlignment = TextAlignment.Right;
        var top = new Grid { ColumnSpacing = 8 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.Children.Add(_totalText);
        Grid.SetColumn(_freeText, 1);
        top.Children.Add(_freeText);

        _bar = new Grid { Height = 8, CornerRadius = new CornerRadius(4), Background = SettingsRows.SystemBrush("ControlStrokeColorDefaultBrush") };
        AutomationProperties.SetName(_bar, "Disk usage");
        _legend = new ChipWrapPanel { Spacing = 12 };
        var summary = new StackPanel { Spacing = 10, Padding = new Thickness(12) };
        summary.Children.Add(top);
        summary.Children.Add(_bar);
        summary.Children.Add(_legend);
        stack.Children.Add(new Border { Style = SettingsRows.StyleOf("HistoryCardStyle"), Child = summary });

        // ── Models: the existing block ────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader("Models"));
        _models = new ModelStorageBlock();
        _models.StorageChanged += () => _ = RefreshAsync();
        stack.Children.Add(_models);

        // ── Your data ─────────────────────────────────────────────────────
        stack.Children.Add(SettingsRows.SectionHeader("Your data"));
        _recordings = new FolderCard(this, "recordings", OpenRecordings, () => _ = ChangeRecordingsAsync(), RestartNow, () => _ = DontMoveRecordingsAsync())
        {
            Extra = RetentionLink(),
        };
        stack.Children.Add(_recordings);
        _data = new FolderCard(this, "data", OpenData, () => _ = ChangeDataAsync(), RestartNow, () => _ = DontMoveDataAsync())
        {
            Margin = new Thickness(0, 8, 0, 0),
        };
        stack.Children.Add(_data);

        stack.Children.Add(SettingsRows.Note(StorageFolderText.Footer(Ctx.Android)));
        RenderCards(measured: null);
        return stack;
    }

    public override void OnShown() => _ = RefreshAsync();

    private UIElement RetentionLink()
    {
        var link = SettingsRows.LinkRow(SettingsRows.Icon(Symbol.Clock), "Retention and limits",
            SettingsText.RetentionLinkSubtitle(Ctx.Live.History), () => Ctx.Navigate(typeof(HistorySettingsPage), null));
        var holder = new StackPanel();
        holder.Children.Add(SettingsRows.Divider());
        holder.Children.Add(link);
        holder.Margin = new Thickness(-12, 4, -12, -12);
        return holder;
    }

    /// <summary>Measures off the UI thread and fills the summary and the cards; a later call wins over an earlier one.</summary>
    public async Task RefreshAsync()
    {
        var version = Interlocked.Increment(ref _version);
        try
        {
            if (_models is not null)
                await _models.RefreshAsync();
            var settings = Ctx.Live;
            var usage = await Task.Run(() => StorageOverview.Measure(settings, AppPaths.DataRoot, AppPaths.SessionsDir, AppPaths.ChatsDir));
            if (version != Volatile.Read(ref _version))
                return;
            RenderSummary(usage);
            RenderCards(usage);
            LastHubLine = StorageOverview.HubLine(usage);
            Ctx.RefreshHub();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Storage could not be measured: {ex.Message}");
        }
    }

    private void RenderSummary(StorageUsage usage)
    {
        if (_totalText is null || _freeText is null || _bar is null || _legend is null)
            return;
        _totalText.Text = StorageOverview.TotalText(usage);
        _freeText.Text = StorageOverview.FreeText(usage.Drives, Ctx.Android);

        var (models, recordings, data) = StorageOverview.Fractions(usage);
        var free = Math.Max(0, 1 - models - recordings - data);
        _bar.ColumnDefinitions.Clear();
        _bar.Children.Clear();
        var dark = ActualTheme == ElementTheme.Dark;
        var segments = new (double Share, Windows.UI.Color Color)[]
        {
            (models, dark ? Rgb(0x60, 0xCD, 0xFF) : Rgb(0x00, 0x5F, 0xB8)),
            (recordings, dark ? Rgb(0xB6, 0x9C, 0xE0) : Rgb(0x87, 0x64, 0xB8)),
            (data, dark ? Rgb(0x4F, 0xD1, 0xD9) : Rgb(0x00, 0xA2, 0xAD)),
        };
        var column = 0;
        foreach (var (share, color) in segments)
        {
            _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(share, 0.0001), GridUnitType.Star) });
            var segment = new Border { Background = new SolidColorBrush(color) };
            Grid.SetColumn(segment, column++);
            _bar.Children.Add(segment);
            if (share <= 0)
                segment.Visibility = Visibility.Collapsed;
        }
        _bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(free, 0.0001), GridUnitType.Star) });

        _legend.Children.Clear();
        var items = new[]
        {
            ("Models", usage.ModelsBytes, segments[0].Color),
            ("Recordings", usage.RecordingsBytes, segments[1].Color),
            ("App data", usage.DataBytes, segments[2].Color),
        };
        for (var i = 0; i < items.Length; i++)
        {
            var (name, bytes, color) = items[i];
            var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            item.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = name, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = HistoryUiFormat.Bytes(bytes), FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            _legend.Children.Add(item);
        }
    }

    private static Windows.UI.Color Rgb(byte r, byte g, byte b) => Windows.UI.Color.FromArgb(0xFF, r, g, b);

    // ── Cards ─────────────────────────────────────────────────────────────

    private void RenderCards(StorageUsage? measured)
    {
        var live = Ctx.Live;

        if (_recordings is not null)
        {
            var configured = RecordingStorageMigrator.Resolve(live, AppPaths.SessionsDir);
            var active = ActiveSessionsDir();
            string? note = null;
            var warn = false;
            var restart = false;
            var dontMove = false;
            if (!string.IsNullOrWhiteSpace(live.PendingSessionsPath))
            {
                note = StorageFolderText.PendingNote(StorageFolderKind.Recordings, live.PendingSessionsPath);
                restart = dontMove = true;
            }
            else if (active is not null && !FolderMover.PathsEqual(active, configured))
            {
                note = StorageFolderText.RecordingsSwitchNote(configured);
                restart = true;
            }
            else if (!string.IsNullOrWhiteSpace(live.SessionsStorageNote))
            {
                note = live.SessionsStorageNote;
                warn = true;
            }

            var facts = measured is null ? "Measuring…" : StorageFolderText.RecordingsFacts(measured.RecordingsBytes, measured.Recordings);
            _recordings.Render(new FolderCardModel(Symbol.Microphone, "Recordings", StorageFolderText.What(StorageFolderKind.Recordings), configured,
                StorageFolderText.Badge(configured, AppPaths.SessionsDir), facts, null, note, warn, restart, dontMove));
        }

        if (_data is not null)
        {
            var root = AppPaths.DataRoot;
            string? note = null;
            var warn = false;
            var restart = false;
            var dontMove = false;
            if (DataLocation.PendingTarget(AppPaths.PointerRoot) is { } pending)
            {
                note = StorageFolderText.PendingNote(StorageFolderKind.Data, pending);
                restart = dontMove = true;
            }
            else if (AppPaths.DataRootNote is { Length: > 0 } dataNote)
            {
                note = dataNote;
                warn = true;
            }

            var facts = measured is null ? "Measuring…" : StorageFolderText.DataFacts(measured.DataBytes);
            _data.Render(new FolderCardModel(Symbol.Library, "App data", StorageFolderText.What(StorageFolderKind.Data), root,
                StorageFolderText.Badge(root, AppPaths.DefaultDataRoot), facts, StorageFolderText.DataContents, note, warn, restart, dontMove));
        }
    }

    /// <summary>The folder the history store opened at start (it cannot change while the app runs).</summary>
    private static string? ActiveSessionsDir()
    {
        try
        {
            return Path.GetFullPath(HistoryServices.Recordings.SessionsDir);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    // ── Actions ───────────────────────────────────────────────────────────

    private void OpenRecordings() => FolderOpener.Open(RecordingStorageMigrator.Resolve(Ctx.Live, AppPaths.SessionsDir));

    private void OpenData() => FolderOpener.Open(AppPaths.DataRoot);

    private static void RestartNow() => AppRestart.RestartNow();

    private async Task ChangeRecordingsAsync()
    {
        try
        {
            var old = RecordingStorageMigrator.Resolve(Ctx.Live, AppPaths.SessionsDir);
            var picked = await StorageFolderUi.PickFolderAsync(XamlRoot, old);
            if (picked is null)
                return;
            var newFolder = Path.GetFullPath(picked);
            var count = await Task.Run(() => HistoryUiStorage.Measure(old));

            var choice = ModelRootChoice.Keep;
            if (RecordingStorageMigrator.NeedsQuestion(old, newFolder, count.Recordings))
            {
                var subject = StorageFolderText.Subject(StorageFolderKind.Recordings, count.Recordings, count.TotalBytes);
                choice = await StorageFolderUi.AskAsync(XamlRoot, StorageFolderKind.Recordings, subject, old, newFolder, StorageFolderText.Relation(old, newFolder));
            }
            if (choice == ModelRootChoice.Cancel)
                return;

            await Ctx.ChangeAsync(s => RecordingStorageMigrator.ApplyChoice(s, newFolder, choice));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Recordings folder change failed: {ex}");
        }
    }

    private async Task DontMoveRecordingsAsync()
    {
        await Ctx.ChangeAsync(RecordingStorageMigrator.CancelPending);
        await RefreshAsync();
    }

    private async Task ChangeDataAsync()
    {
        try
        {
            var old = AppPaths.DataRoot;
            var picked = await StorageFolderUi.PickFolderAsync(XamlRoot, old);
            if (picked is null)
                return;
            var newFolder = Path.GetFullPath(picked);
            if (FolderMover.PathsEqual(old, newFolder))
                return;

            var bytes = await Task.Run(() => StorageOverview.FolderSize(old, []));
            var subject = StorageFolderText.Subject(StorageFolderKind.Data, 0, bytes);
            var choice = await StorageFolderUi.AskAsync(XamlRoot, StorageFolderKind.Data, subject, old, newFolder, StorageFolderText.Relation(old, newFolder));
            if (choice != ModelRootChoice.MoveOnRestart)
                return;

            // The pointer file lives in the default root; the next start moves the data and flips it.
            await Task.Run(() => DataLocation.RequestMove(AppPaths.PointerRoot, newFolder));
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] App data folder change failed: {ex}");
        }
    }

    private async Task DontMoveDataAsync()
    {
        await Task.Run(() => DataLocation.CancelMove(AppPaths.PointerRoot));
        await RefreshAsync();
    }
}
