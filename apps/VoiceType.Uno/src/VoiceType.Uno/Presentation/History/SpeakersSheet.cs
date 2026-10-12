using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using VoiceType.Uno.History;
using VoiceType.Uno.Presentation.Controls;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Speakers sheet (design §10.8, mockup <c>*-speakers-sheet</c>, <c>*-speakers-merge</c>): every person with a colour
/// button, an editable name, talk time / share / turns and a ⋯ menu (Show only, Merge into…, Split, Change colour).
/// Each change goes straight to <see cref="ISpeakerService"/>; the list redraws from the service afterwards.
/// A person who talks very little gets a "same voice?" hint. Footer: Detect again… and Done.
/// </summary>
internal sealed partial class SpeakersSheet : ContentDialog
{
    private readonly ISpeakerService _service;
    private readonly string _recordingId;
    private readonly string _versionId;
    private readonly StackPanel _list = new() { Spacing = 4 };
    private readonly TextBlock _subtitle = new() { FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _banner = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Button _cancelMerge = new() { Content = "Cancel merge", MinHeight = 44, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private string? _mergeFrom;
    private bool _rebuilding;

    private SpeakersSheet(ISpeakerService service, string recordingId, string versionId)
    {
        _service = service;
        _recordingId = recordingId;
        _versionId = versionId;

        Title = "Speakers";
        PrimaryButtonText = "Detect again…";
        CloseButtonText = "Done";
        DefaultButton = ContentDialogButton.Close;

        var panel = new StackPanel { Spacing = 8, MinWidth = 280, MaxWidth = 520, Padding = new Thickness(0, 0, 8, 0) };
        panel.Children.Add(_subtitle);
        panel.Children.Add(_banner);
        panel.Children.Add(_cancelMerge);
        panel.Children.Add(_list);
        panel.Children.Add(new TextBlock
        {
            Text = "The detection itself is never changed: renaming, colours and merging only change how people are shown. Split undoes a merge.",
            FontSize = 11,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
        });
        Content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            MaxHeight = 520,
        };
        _cancelMerge.Click += (_, _) =>
        {
            _mergeFrom = null;
            Rebuild();
        };
        _service.Changed += OnServiceChanged;
        Closed += (_, _) => _service.Changed -= OnServiceChanged;
        Rebuild();
    }

    /// <summary>Set when the user chose "Show only" for a person (null = nothing asked).</summary>
    public string? FilterRequest { get; private set; }

    /// <summary>Detect again… was pressed: the caller opens the Reprocess sheet with Speech off.</summary>
    public bool DetectAgainRequested { get; private set; }

    public static async Task<SpeakersSheet?> ShowAsync(XamlRoot? xamlRoot, string recordingId, string versionId)
    {
        if (SpeakerServices.Current is not { } service)
            return null;
        var sheet = new SpeakersSheet(service, recordingId, versionId) { XamlRoot = xamlRoot };
        var result = await HistoryDialogs.ShowSafeAsync(sheet);
        sheet.DetectAgainRequested = result == ContentDialogResult.Primary;
        return sheet;
    }

    /// <summary>Rename dialog for one person (tap on the name above a transcript run).</summary>
    public static async Task RenameAsync(XamlRoot? xamlRoot, string recordingId, string versionId, string personId)
    {
        if (SpeakerServices.Current is not { } service || SpeakersUi.PersonOf(service.GetLayer(recordingId, versionId), personId) is not { } person)
            return;
        var box = new TextBox { Text = person.Name, MinHeight = 44, MaxLength = 60 };
        AutomationProperties.SetName(box, "Speaker name");
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Rename speaker",
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 440,
                Children =
                {
                    box,
                    new TextBlock { Text = "The name is used everywhere this recording shows the speaker: transcript, exports and chats.", FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap },
                },
            },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        if (await HistoryDialogs.ShowSafeAsync(dialog) != ContentDialogResult.Primary)
            return;
        var name = box.Text.Trim();
        if (name.Length > 0 && name != person.Name)
            service.RenamePerson(recordingId, personId, name);
    }

    private void OnServiceChanged(string recordingId)
    {
        if (recordingId != _recordingId)
            return;
        DispatcherQueue.TryEnqueue(Rebuild);
    }

    private SpeakerLayerView? Layer => _service.GetLayer(_recordingId, _versionId);

    private void Rebuild()
    {
        _rebuilding = true;
        try
        {
            RebuildCore();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void RebuildCore()
    {
        _list.Children.Clear();
        var layer = Layer;
        if (layer is null)
        {
            _subtitle.Text = "No speakers detected for this version yet.";
            return;
        }

        _subtitle.Text = SpeakersUi.VersionLine(layer) + (SpeakersUi.PresetLine(layer) is { Length: > 0 } preset ? " · " + preset : "");
        var warning = SpeakersUi.VoiceLimitWarning(layer);
        var merging = _mergeFrom is not null && SpeakersUi.PersonOf(layer, _mergeFrom) is not null;
        if (!merging)
            _mergeFrom = null;
        _banner.Visibility = merging || warning is not null ? Visibility.Visible : Visibility.Collapsed;
        _banner.Text = merging ? $"Merge {SpeakersUi.NameOf(layer, _mergeFrom)} into… Pick the person to keep." : warning ?? "";
        _cancelMerge.Visibility = merging ? Visibility.Visible : Visibility.Collapsed;

        foreach (var person in SpeakersUi.ByTalkTime(layer))
            _list.Children.Add(Row(layer, person, merging));
    }

    private UIElement Row(SpeakerLayerView layer, SpeakerPerson person, bool merging)
    {
        var isSource = merging && person.Id == _mergeFrom;
        var grid = new Grid { ColumnSpacing = 8, MinHeight = 56 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Colour button 44×44: the face is the person's colour, a tap opens the palette.
        var swatch = new SpeakerSwatch { ColorIndex = person.Color, Width = 28, Height = 28, CornerRadius = new CornerRadius(14), IsHitTestVisible = false };
        var colorButton = new Button
        {
            Content = swatch,
            Width = 44,
            Height = 44,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            IsEnabled = !merging,
        };
        AutomationProperties.SetName(colorButton, $"Colour of {person.Name}: {SpeakerSwatch.NameOf(person.Color)}. Change");
        colorButton.Click += (_, _) => ShowPalette(colorButton, person);
        grid.Children.Add(colorButton);

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var stat = SpeakersUi.StatOf(layer, person.Id);
        if (merging && !isSource)
        {
            text.Children.Add(new TextBlock { Text = person.Name, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            var box = new TextBox { Text = person.Name, MinHeight = 44, IsEnabled = !merging, MaxLength = 60 };
            AutomationProperties.SetName(box, "Name of " + person.Name);
            void Commit()
            {
                if (_rebuilding)
                    return;
                var name = box.Text.Trim();
                var current = SpeakersUi.PersonOf(Layer, person.Id)?.Name ?? person.Name;
                if (name.Length > 0 && name != current)
                    _service.RenamePerson(_recordingId, person.Id, name);
                else
                    box.Text = person.Name;
            }
            box.LostFocus += (_, _) => Commit();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    e.Handled = true;
                    Commit();
                }
            };
            text.Children.Add(box);
        }
        text.Children.Add(new TextBlock { Text = SpeakersUi.StatLine(stat), FontSize = 12, Opacity = 0.75 });
        if (!merging && SpeakersUi.MergeHint(layer, person) is { } hint)
        {
            text.Children.Add(new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] });
            var merge = new Button { Content = "Merge…", MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(merge, $"Merge {person.Name} into another speaker");
            merge.Click += (_, _) => BeginMerge(person);
            text.Children.Add(merge);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (merging && !isSource)
        {
            var keep = new Button { Content = "Merge here", MinHeight = 44, VerticalAlignment = VerticalAlignment.Top, Style = AccentStyle() };
            AutomationProperties.SetName(keep, $"Merge into {person.Name}");
            keep.Click += (_, _) =>
            {
                var from = _mergeFrom!;
                _mergeFrom = null;
                _service.MergePeople(_recordingId, person.Id, from);
                Rebuild();
            };
            Grid.SetColumn(keep, 2);
            grid.Children.Add(keep);
        }
        else if (!merging)
        {
            var more = new Button
            {
                Content = new SymbolIcon(Symbol.More),
                Width = 44,
                Height = 44,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Top,
            };
            AutomationProperties.SetName(more, $"{person.Name}: more actions");
            more.Click += (_, _) => ShowMore(more, layer, person);
            Grid.SetColumn(more, 2);
            grid.Children.Add(more);
        }

        var card = new Border
        {
            Padding = new Thickness(4, 4, 4, 4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Child = grid,
        };
        return card;
    }

    private static Style? AccentStyle() =>
        Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style) ? style as Style : null;

    private void BeginMerge(SpeakerPerson person)
    {
        _mergeFrom = person.Id;
        Rebuild();
    }

    private void ShowMore(FrameworkElement anchor, SpeakerLayerView layer, SpeakerPerson person)
    {
        var menu = new MenuFlyout();
        var only = new MenuFlyoutItem { Text = "Show only " + person.Name, Icon = new SymbolIcon(Symbol.Filter), MinHeight = 44 };
        only.Click += (_, _) =>
        {
            FilterRequest = person.Id;
            Hide();
        };
        menu.Items.Add(only);
        var merge = new MenuFlyoutItem { Text = "Merge into…", Icon = new SymbolIcon(Symbol.Sync), MinHeight = 44, IsEnabled = layer.People.Count > 1 };
        merge.Click += (_, _) => BeginMerge(person);
        menu.Items.Add(merge);
        var split = new MenuFlyoutItem { Text = "Split (undo a merge)", Icon = new SymbolIcon(Symbol.Undo), MinHeight = 44 };
        split.Click += (_, _) =>
        {
            _service.SplitPerson(_recordingId, person.Id);
            Rebuild();
        };
        menu.Items.Add(split);
        var colour = new MenuFlyoutItem { Text = "Change colour", Icon = new SymbolIcon(Symbol.FontColor), MinHeight = 44 };
        colour.Click += (_, _) => ShowPalette(anchor, person);
        menu.Items.Add(colour);
        menu.ShowAt(anchor);
    }

    /// <summary>The 8-colour palette: a 4 × 2 grid of 44 px buttons with the colour name as accessible name.</summary>
    private void ShowPalette(FrameworkElement anchor, SpeakerPerson person)
    {
        var grid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
        for (var c = 0; c < 4; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < 2; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var flyout = new Flyout { Content = grid };
        for (var i = 0; i < SpeakersUi.PaletteSize; i++)
        {
            var index = i;
            var selected = SpeakersUi.NormalizeColor(person.Color) == index;
            var face = new Grid { Width = 32, Height = 32 };
            face.Children.Add(new SpeakerSwatch { ColorIndex = index, CornerRadius = new CornerRadius(16), IsHitTestVisible = false });
            if (selected)
                face.Children.Add(new FontIcon { Glyph = "", FontSize = 14, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), IsHitTestVisible = false });
            var button = new Button { Content = face, Width = 44, Height = 44, Padding = new Thickness(0) };
            AutomationProperties.SetName(button, SpeakerSwatch.NameOf(index) + (selected ? ", selected" : ""));
            button.Click += (_, _) =>
            {
                flyout.Hide();
                _service.SetPersonColor(_recordingId, person.Id, index);
                Rebuild();
            };
            Grid.SetRow(button, i / 4);
            Grid.SetColumn(button, i % 4);
            grid.Children.Add(button);
        }
        flyout.ShowAt(anchor);
    }
}
