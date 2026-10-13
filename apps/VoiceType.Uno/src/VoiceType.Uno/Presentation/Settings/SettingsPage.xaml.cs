using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.History;
using VoiceType.Uno.Services;
using VirtualKey = Windows.System.VirtualKey;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>
/// Settings (design voicetype-uno-settings): <c>Frame.Navigate(typeof(SettingsPage))</c>, optionally with a
/// <see cref="SettingsSection"/> to open. Under 840 px the section list (hub) and a section take turns; wider, they sit side by side.
/// </summary>
public sealed partial class SettingsPage : Page
{
    /// <summary>The section last open, so coming back from Settings › History or Models lands where the user was.</summary>
    private static SettingsSection? s_lastSection;

    private readonly Dictionary<SettingsSection, (Button Button, TextBlock Subtitle)> _navItems = [];
    private readonly List<FontIcon> _sectionChevrons = [];
    private SettingsContext? _context;
    private SettingsSectionView? _view;
    private SettingsSection _shown = SettingsSection.Storage;
    private bool _showHub = true;
    private bool _wide;
    private bool _built;
    private TextBlock? _historySubtitle;
#if !VOICE_TYPE_WINDOWS
    private Windows.UI.Core.SystemNavigationManager? _systemNavigation;
#endif

    public SettingsPage()
    {
        InitializeComponent();
        ActualThemeChanged += (_, _) => RebuildAfterThemeChange();
    }

    private static bool Android => PlatformUi.IsAndroidLayout;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _context = new SettingsContext(this, (type, parameter) => Frame?.Navigate(type, parameter), RefreshHub);

        if (e.Parameter is SettingsSection requested)
        {
            _shown = SettingsText.Coerce(requested, Android);
            _showHub = false;
        }
        else if (e.NavigationMode == NavigationMode.Back && s_lastSection is { } last)
        {
            _shown = SettingsText.Coerce(last, Android);
            _showHub = false;
        }
        else
        {
            _shown = SettingsText.InitialSection(wide: true) ?? SettingsSection.Storage;
            _showHub = true;
        }

        BuildList();
        ShowSection(_shown);
        SubscribeSystemBack();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _view?.OnHidden();
        UnsubscribeSystemBack();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout();
        BackButton.Focus(FocusState.Programmatic);
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    // ── Layout: hub / section on narrow, two panes on wide ────────────────

    private void ApplyLayout()
    {
        _wide = SettingsText.IsWide(ActualWidth);
        var star = new GridLength(1, GridUnitType.Star);
        var showList = _wide || _showHub;
        var showPane = _wide || !_showHub;

        ListColumn.Width = _wide ? new GridLength(SettingsText.ListWidth) : showList ? star : new GridLength(0);
        DividerColumn.Width = _wide ? new GridLength(1) : new GridLength(0);
        PaneColumn.Width = showPane ? star : new GridLength(0);
        ListScroll.Visibility = showList ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = _wide ? Visibility.Visible : Visibility.Collapsed;
        PaneScroll.Visibility = showPane ? Visibility.Visible : Visibility.Collapsed;
        PaneTitle.Visibility = _wide ? Visibility.Visible : Visibility.Collapsed;

        var narrowSection = !_wide && !_showHub;
        TitleText.Text = SettingsText.HeaderTitle(narrowSection ? _shown : null, _wide);
        SubtitleText.Text = SettingsText.HeaderSubtitle(narrowSection ? _shown : null, _wide);
        AutomationProperties.SetName(BackButton, narrowSection ? "Back to Settings" : "Close Settings");
        ToolTipService.SetToolTip(BackButton, narrowSection ? "Back to Settings" : "Close Settings");
        // Side by side the list only selects (no chevron); on its own it opens a section (chevron).
        foreach (var chevron in _sectionChevrons)
            chevron.Visibility = _wide ? Visibility.Collapsed : Visibility.Visible;
        UpdateSelection();
    }

    private static FontIcon ChevronOf(Button button) => ((Grid)button.Content).Children.OfType<FontIcon>().First();

    private void UpdateSelection()
    {
        foreach (var (section, item) in _navItems)
        {
            var selected = _wide && section == _shown;
            item.Button.Background = selected ? SettingsRows.ThemeBrush(this, "HistorySelectedBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            item.Button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
    }

    // ── The section list ──────────────────────────────────────────────────

    private void BuildList()
    {
        ListPanel.Children.Clear();
        _navItems.Clear();
        _sectionChevrons.Clear();
        ListPanel.Children.Add(SettingsRows.SectionHeader("App"));
        foreach (var section in SettingsText.VisibleSections(Android))
        {
            var (button, subtitle) = NavButton(IconOf(section), SettingsText.Title(section), "", () => ShowSection(section, fromList: true), chevron: true);
            _navItems[section] = (button, subtitle);
            _sectionChevrons.Add(ChevronOf(button));
            ListPanel.Children.Add(button);
        }

        ListPanel.Children.Add(SettingsRows.SectionHeader("Also in Settings"));
        var history = NavButton(Microsoft.UI.Xaml.Controls.Symbol.Clock, "History", "", () => Frame?.Navigate(typeof(HistorySettingsPage)), chevron: true);
        _historySubtitle = history.Subtitle;
        ListPanel.Children.Add(history.Button);
        var models = NavButton(Microsoft.UI.Xaml.Controls.Symbol.Download, "Models", SettingsText.ModelsLinkSubtitle, () => Frame?.Navigate(typeof(ModelsPage), ModelsTab.Speech), chevron: true);
        ListPanel.Children.Add(models.Button);
        _built = true;
        RefreshHub();
    }

    private (Button Button, TextBlock Subtitle) NavButton(Symbol icon, string title, string subtitle, Action click, bool chevron)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(SettingsRows.Icon(icon));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        var sub = SettingsRows.Secondary(subtitle);
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(sub);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (chevron)
        {
            var arrow = SettingsRows.Chevron();
            Grid.SetColumn(arrow, 2);
            grid.Children.Add(arrow);
        }

        var button = new Button { Style = SettingsRows.StyleOf("HistoryRowButtonStyle"), MinHeight = 56, Content = grid };
        AutomationProperties.SetName(button, title);
        button.Click += (_, _) => click();
        return (button, sub);
    }

    private static Symbol IconOf(SettingsSection section) => section switch
    {
        SettingsSection.Storage => Microsoft.UI.Xaml.Controls.Symbol.Library,
        SettingsSection.Speech => Microsoft.UI.Xaml.Controls.Symbol.Microphone,
        SettingsSection.Audio => Microsoft.UI.Xaml.Controls.Symbol.Volume,
        SettingsSection.Translation => Microsoft.UI.Xaml.Controls.Symbol.Globe,
        SettingsSection.Typing => Microsoft.UI.Xaml.Controls.Symbol.Keyboard,
        SettingsSection.Hotkeys => Microsoft.UI.Xaml.Controls.Symbol.SwitchApps,
        _ => Microsoft.UI.Xaml.Controls.Symbol.Setting,
    };

    /// <summary>Refreshes the current-value subtitles of the list (called after every change).</summary>
    private void RefreshHub()
    {
        if (!_built || _context is null)
            return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(RefreshHub);
            return;
        }

        var settings = _context.Live;
        var context = new SettingsText.SubtitleContext(Android, StorageSection.LastHubLine);
        foreach (var (section, item) in _navItems)
            item.Subtitle.Text = SettingsText.Subtitle(section, settings, context);
        if (_historySubtitle is not null)
            _historySubtitle.Text = SettingsText.HistoryLinkSubtitle(settings.History);
    }

    // ── Showing a section ─────────────────────────────────────────────────

    private void ShowSection(SettingsSection section, bool fromList = false)
    {
        if (_context is null)
            return;
        _view?.OnHidden();
        _shown = section;
        s_lastSection = section;
        if (fromList)
            _showHub = false;

        _view = SettingsSectionView.Create(section, _context);
        SectionHost.Content = _view;
        PaneTitle.Text = SettingsText.Title(section);
        PaneScroll.ChangeView(null, 0, null, disableAnimation: true);
        ApplyLayout();
        _view.OnShown();
    }

    private void RebuildAfterThemeChange()
    {
        if (_context is null || !_built)
            return;
        BuildList();
        ShowSection(_shown);
    }

    // ── Back ──────────────────────────────────────────────────────────────

    private bool GoBackWithinSettings()
    {
        if (_wide || _showHub)
            return false;
        _showHub = true;
        ApplyLayout();
        return true;
    }

    private void GoBack()
    {
        if (GoBackWithinSettings())
            return;
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

    private void SubscribeSystemBack()
    {
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

    private void UnsubscribeSystemBack()
    {
#if !VOICE_TYPE_WINDOWS
        if (_systemNavigation is not null)
            _systemNavigation.BackRequested -= OnSystemBackRequested;
        _systemNavigation = null;
#endif
    }

#if !VOICE_TYPE_WINDOWS
    private void OnSystemBackRequested(object? sender, Windows.UI.Core.BackRequestedEventArgs e)
    {
        if (e.Handled)
            return;
        if (GoBackWithinSettings())
        {
            e.Handled = true;
            return;
        }
        if (Frame?.CanGoBack == true)
        {
            e.Handled = true;
            Frame.GoBack();
        }
    }
#endif
}
