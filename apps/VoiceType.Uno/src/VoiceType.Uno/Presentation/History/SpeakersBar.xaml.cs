using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.Controls;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// The speaker row of the Transcript tab (see SpeakersBar.xaml). Pure view: <see cref="Update"/> shows the
/// state of a <see cref="RecordingViewModel"/> and the events say what the user asked for.
/// </summary>
public sealed partial class SpeakersBar : UserControl
{
    private readonly List<UIElement> _chips = [];
    private bool _updating;
    private SpeakerDetectKind _detectKind;

    public SpeakersBar() => InitializeComponent();

    /// <summary>Open the Speakers sheet (rename, colour, merge).</summary>
    public event EventHandler? OpenSheetRequested;

    /// <summary>Filter by a speaker (id) or show everyone (null).</summary>
    public event Action<string?>? FilterRequested;

    public event EventHandler? DetectRequested;
    /// <summary>One tap on "Download" of the missing speaker model.</summary>
    public event EventHandler? DownloadModelRequested;

    public event EventHandler? CancelModelDownloadRequested;
    public event Action<bool>? ColorWordsRequested;

    public void Update(RecordingViewModel vm, double width, double height)
    {
        _updating = true;
        try
        {
            var layer = vm.Layer;
            var mode = SpeakersUi.BarMode(width, height);
            SummaryRow.Visibility = layer is not null && mode == SpeakerBarMode.Summary ? Visibility.Visible : Visibility.Collapsed;
            PickerRow.Visibility = layer is not null && mode == SpeakerBarMode.Picker ? Visibility.Visible : Visibility.Collapsed;
            ChipsRow.Visibility = layer is not null && mode == SpeakerBarMode.Chips ? Visibility.Visible : Visibility.Collapsed;

            var colorToggle = vm.HasWordLabels ? Visibility.Visible : Visibility.Collapsed;
            foreach (var toggle in new[] { SummaryColorToggle, PickerColorToggle, ChipsColorToggle })
            {
                toggle.Visibility = colorToggle;
                toggle.IsChecked = vm.ColorWords;
            }

            if (layer is not null)
            {
                var summary = SpeakersUi.SummaryLine(layer);
                SummaryText.Text = summary;
                AutomationProperties.SetHelpText(SummaryButton, summary);
                var filtered = vm.IsFilterActive ? SpeakersUi.NameOf(layer, vm.FilterPersonId) : null;
                PickerText.Text = filtered ?? SpeakersUi.SpeakerCount(layer.People.Count);
                BuildChips(vm, layer);
            }

            FilterRow.Visibility = vm.IsFilterActive ? Visibility.Visible : Visibility.Collapsed;
            FilterText.Text = vm.FilterLine;
            FilterSwatch.ColorIndex = vm.FilterColor;

            var detect = vm.Detect;
            _detectKind = detect.Kind;
            DetectCard.Visibility = detect.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            DetectTitle.Text = detect.Title;
            DetectDetail.Text = detect.Detail;
            DetectButton.Content = detect.ButtonText;
            DetectButton.IsEnabled = detect.ButtonEnabled;
            DetectButton.Visibility = detect.Kind is SpeakerDetectKind.Queued or SpeakerDetectKind.Running ? Visibility.Collapsed : Visibility.Visible;
            DetectProgress.Visibility = detect.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
            DetectProgress.IsIndeterminate = detect.ShowProgress && detect.Progress <= 0;
            DetectProgress.Value = detect.Progress;
            AutomationProperties.SetName(DetectButton, detect.ButtonText);
        }
        finally
        {
            _updating = false;
        }
    }

    private void BuildChips(RecordingViewModel vm, SpeakerLayerView layer)
    {
        foreach (var chip in _chips)
            ChipsRow.Children.Remove(chip);
        _chips.Clear();

        var all = Chip("All speakers", null, selected: !vm.IsFilterActive, "Show all speakers");
        Insert(all);
        foreach (var person in SpeakersUi.ByTalkTime(layer))
        {
            var text = SpeakersUi.ChipText(layer, person);
            var selected = vm.IsFilterActive && vm.FilterPersonId == person.Id;
            var id = person.Id;
            var chip = Chip(text, person.Color, selected, selected ? $"{person.Name}, showing only this speaker" : $"Show only {person.Name}");
            chip.Click += (_, _) => FilterRequested?.Invoke(selected ? null : id);
            Insert(chip);
        }
        all.Click += (_, _) => FilterRequested?.Invoke(null);
    }

    private void Insert(Button chip)
    {
        // Chips go before the colour toggle and the manage button, which stay the last two children.
        ChipsRow.Children.Insert(ChipsRow.Children.Count - 2, chip);
        _chips.Add(chip);
    }

    private Button Chip(string text, int? colorIndex, bool selected, string automationName)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (colorIndex is { } color)
            content.Children.Add(new SpeakerSwatch { ColorIndex = color, Width = 10, Height = 10, CornerRadius = new CornerRadius(5), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = text, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            MinHeight = 44,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(22),
        };
        if (selected)
        {
            button.Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            if (content.Children[^1] is TextBlock label)
            {
                label.Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"];
                label.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            }
        }
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    private void OpenSheet_Click(object sender, RoutedEventArgs e) => OpenSheetRequested?.Invoke(this, EventArgs.Empty);

    private void ClearFilter_Click(object sender, RoutedEventArgs e) => FilterRequested?.Invoke(null);

    private void ColorToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!_updating && sender is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton toggle)
            ColorWordsRequested?.Invoke(toggle.IsChecked == true);
    }

    private void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (_detectKind == SpeakerDetectKind.NoModel)
            DownloadModelRequested?.Invoke(this, EventArgs.Empty);
        else if (_detectKind == SpeakerDetectKind.DownloadingModel)
            CancelModelDownloadRequested?.Invoke(this, EventArgs.Empty);
        else
            DetectRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The 600–839 px picker: All speakers, one entry per person, then the sheet.</summary>
    private void Picker_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RecordingViewModel vm || vm.Layer is not { } layer)
            return;
        var menu = new MenuFlyout();
        var all = new ToggleMenuFlyoutItem { Text = "All speakers", IsChecked = !vm.IsFilterActive, MinHeight = 44 };
        all.Click += (_, _) => FilterRequested?.Invoke(null);
        menu.Items.Add(all);
        foreach (var person in SpeakersUi.ByTalkTime(layer))
        {
            var id = person.Id;
            var item = new ToggleMenuFlyoutItem
            {
                Text = "● " + SpeakersUi.ChipText(layer, person),
                IsChecked = vm.IsFilterActive && vm.FilterPersonId == id,
                MinHeight = 44,
                Foreground = SpeakerServices.BrushFor(person.Color, this),
            };
            item.Click += (_, _) => FilterRequested?.Invoke(id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var manage = new MenuFlyoutItem { Text = "Speakers…", MinHeight = 44 };
        manage.Click += (_, _) => OpenSheetRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(manage);
        menu.ShowAt((FrameworkElement)sender);
    }
}
