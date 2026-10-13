using Microsoft.UI.Xaml.Automation;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>What a folder card shows: the same anatomy for Recordings and App data (title, what is inside, badge, full path, size, note, two buttons).</summary>
internal sealed record FolderCardModel(
    Symbol Icon,
    string Title,
    string What,
    string Path,
    string Badge,
    string Facts,
    IReadOnlyList<(string Name, string Meaning)>? Contents = null,
    string? Note = null,
    bool NoteIsWarning = false,
    bool NoteHasRestart = false,
    bool NoteHasDontMove = false);

/// <summary>
/// A folder card of Settings › Storage (design §4): header with a Default/Custom badge, the folder path in a wrapping
/// monospace box (never an ellipsis), size, the pending/incomplete note, and Open folder · Change folder… (desktop only).
/// </summary>
internal sealed class FolderCard : UserControl
{
    private readonly FrameworkElement _scope;
    private readonly string _key;
    private readonly Action _open;
    private readonly Action _change;
    private readonly Action _restart;
    private readonly Action _dontMove;
    private readonly StackPanel _body = new() { Spacing = 8, Padding = new Thickness(12) };

    public FolderCard(FrameworkElement scope, string key, Action open, Action change, Action restart, Action dontMove)
    {
        _scope = scope;
        _key = key;
        _open = open;
        _change = change;
        _restart = restart;
        _dontMove = dontMove;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Content = new Border { Style = SettingsRows.StyleOf("HistoryCardStyle"), Child = _body };
    }

    /// <summary>A row under the buttons (the retention link of Recordings).</summary>
    public UIElement? Extra { get; set; }

    public void Render(FolderCardModel model)
    {
        _body.Children.Clear();

        // Header: icon, title with what is inside, badge.
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = SettingsRows.Icon(model.Icon);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        head.Children.Add(icon);
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = model.Title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(SettingsRows.Secondary(model.What));
        Grid.SetColumn(text, 1);
        head.Children.Add(text);
        var badge = new Border
        {
            Style = SettingsRows.StyleOf("HistoryChipStyle"),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = model.Badge, Style = SettingsRows.StyleOf("HistoryChipTextStyle") },
        };
        if (model.Badge == "Custom")
            badge.Background = SettingsRows.ThemeBrush(_scope, "HistoryHintBrush");
        Grid.SetColumn(badge, 2);
        head.Children.Add(badge);
        _body.Children.Add(head);

        _body.Children.Add(SettingsRows.PathBox(model.Path, $"{model.Title} folder path"));

        var facts = new TextBlock { Text = model.Facts, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        _body.Children.Add(facts);

        if (model.Contents is { Count: > 0 } contents)
        {
            var list = new Grid { ColumnSpacing = 12, RowSpacing = 2 };
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            list.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < contents.Count; i++)
            {
                list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = new TextBlock { Text = contents[i].Name, FontFamily = SettingsRows.MonoFont, FontSize = 12 };
                var meaning = SettingsRows.Secondary(contents[i].Meaning);
                Grid.SetRow(name, i);
                Grid.SetRow(meaning, i);
                Grid.SetColumn(meaning, 1);
                list.Children.Add(name);
                list.Children.Add(meaning);
            }
            _body.Children.Add(list);
        }

        if (!string.IsNullOrEmpty(model.Note))
        {
            var buttons = new List<(string, Action)>();
            if (model.NoteHasRestart && AppRestart.IsAvailable)
                buttons.Add((StorageFolderText.RestartNow, _restart));
            if (model.NoteHasDontMove)
                buttons.Add((StorageFolderText.DontMove, _dontMove));
            _body.Children.Add(SettingsRows.Callout(_scope, model.Note, model.NoteIsWarning, [.. buttons]));
        }

        // Phones keep everything in the app's private storage: no file manager to open, no folder to pick.
        if (!PlatformUi.IsAndroidLayout)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(ActionButton(StorageFolderText.OpenFolder, $"Open the {model.Title} folder", _open, 0));
            row.Children.Add(ActionButton(StorageFolderText.ChangeFolder, $"Change the {model.Title} folder", _change, 1));
            _body.Children.Add(row);
        }

        if (Extra is not null)
            _body.Children.Add(Extra);
    }

    private static Button ActionButton(string label, string automationName, Action click, int column)
    {
        var button = new Button
        {
            Style = SettingsRows.StyleOf("HistoryActionButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        };
        AutomationProperties.SetName(button, automationName);
        button.Click += (_, _) => click();
        Grid.SetColumn(button, column);
        return button;
    }
}
