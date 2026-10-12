using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Filter sheet of the History list: "only" switches, language, speech model, sort.
/// The primary button shows the live match count (<c>Show 6</c>); Reset clears everything.
/// </summary>
internal sealed partial class HistoryFilterDialog : ContentDialog
{
    private readonly Func<HistoryUiFilterOptions, int> _count;
    private readonly ToggleSwitch _translation;
    private readonly ToggleSwitch _chat;
    private readonly ToggleSwitch _audio;
    private readonly ToggleSwitch _inject;
    private readonly ComboBox _language;
    private readonly ComboBox _model;
    private readonly ComboBox _sort;
    private readonly IReadOnlyList<HistoryUiChoice> _languages;
    private readonly IReadOnlyList<HistoryUiChoice> _models;
    private readonly TextBlock _subtitle;
    private readonly int _total;

    public HistoryFilterDialog(
        HistoryUiFilterOptions current,
        IReadOnlyList<HistoryUiChoice> languages,
        IReadOnlyList<HistoryUiChoice> models,
        int total,
        Func<HistoryUiFilterOptions, int> count)
    {
        _count = count;
        _languages = languages;
        _models = models;
        _total = total;
        Options = current;

        Title = "Filter recordings";
        PrimaryButtonText = "Show";
        SecondaryButtonText = "Reset";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var panel = new StackPanel { Spacing = 4, MaxWidth = 440 };
        _subtitle = new TextBlock { FontSize = 12, Opacity = 0.75 };
        panel.Children.Add(_subtitle);
        panel.Children.Add(Header("SHOW"));
        panel.Children.Add(SwitchRow("With translation", null, current.WithTranslation, out _translation));
        panel.Children.Add(SwitchRow("Used in a chat", null, current.InChat, out _chat));
        panel.Children.Add(SwitchRow("Audio available", null, current.WithAudio, out _audio));
        panel.Children.Add(SwitchRow("Inject text dictations", "Short phrases typed into other apps", current.InjectOnly, out _inject));

        panel.Children.Add(Header("LANGUAGE"));
        _language = Combo("Language", languages.Select(c => c.Label), IndexOf(languages, current.Language));
        panel.Children.Add(_language);
        panel.Children.Add(Header("SPEECH MODEL"));
        _model = Combo("Speech model", models.Select(c => c.Label), IndexOf(models, current.Model));
        panel.Children.Add(_model);
        panel.Children.Add(Header("SORT"));
        var sorts = Enum.GetValues<HistoryUiSort>();
        _sort = Combo("Sort", sorts.Select(HistoryUiFilterOptions.SortLabel), Array.IndexOf(sorts, current.Sort));
        panel.Children.Add(_sort);

        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        foreach (var toggle in new[] { _translation, _chat, _audio, _inject })
            toggle.Toggled += (_, _) => Update();
        foreach (var combo in new[] { _language, _model, _sort })
            combo.SelectionChanged += (_, _) => Update();
        SecondaryButtonClick += (_, e) =>
        {
            // Reset keeps the sheet open with everything cleared.
            e.Cancel = true;
            _translation.IsOn = _chat.IsOn = _audio.IsOn = _inject.IsOn = false;
            _language.SelectedIndex = _model.SelectedIndex = _sort.SelectedIndex = 0;
            Update();
        };
        Update();
    }

    /// <summary>The options as edited (valid after the primary button).</summary>
    public HistoryUiFilterOptions Options { get; private set; }

    private void Update()
    {
        var model = Pick(_models, _model.SelectedIndex);
        Options = new HistoryUiFilterOptions
        {
            WithTranslation = _translation.IsOn,
            InChat = _chat.IsOn,
            WithAudio = _audio.IsOn,
            InjectOnly = _inject.IsOn,
            Language = Pick(_languages, _language.SelectedIndex)?.Value,
            Model = model?.Value,
            ModelLabel = model?.Value is null ? null : model.Label,
            Sort = _sort.SelectedIndex >= 0 ? Enum.GetValues<HistoryUiSort>()[_sort.SelectedIndex] : HistoryUiSort.NewestFirst,
        };
        var matches = _count(Options);
        _subtitle.Text = $"{HistoryUiFormat.Count(_total, "recording")} · {matches} match";
        PrimaryButtonText = "Show " + matches;
    }

    private static HistoryUiChoice? Pick(IReadOnlyList<HistoryUiChoice> choices, int index) =>
        index >= 0 && index < choices.Count ? choices[index] : null;

    private static int IndexOf(IReadOnlyList<HistoryUiChoice> choices, string? value)
    {
        for (var i = 0; i < choices.Count; i++)
            if (string.Equals(choices[i].Value, value, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Opacity = 0.7,
        Margin = new Thickness(0, 12, 0, 2),
    };

    private static Grid SwitchRow(string title, string? detail, bool isOn, out ToggleSwitch toggle)
    {
        var row = new Grid { ColumnSpacing = 12, MinHeight = 48 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (detail is not null)
            label.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        row.Children.Add(label);
        toggle = new ToggleSwitch
        {
            OnContent = "", OffContent = "", MinWidth = 0, MinHeight = 48, IsOn = isOn,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Stretch,
        };
        AutomationProperties.SetName(toggle, title);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private static ComboBox Combo(string name, IEnumerable<string> items, int selected)
    {
        var combo = new ComboBox { MinHeight = 44, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var item in items)
            combo.Items.Add(item);
        combo.SelectedIndex = Math.Max(0, selected);
        AutomationProperties.SetName(combo, name);
        return combo;
    }
}
