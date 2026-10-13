using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Media;
using VoiceType.Uno.Presentation.History;
using VoiceType.Uno.Services;

namespace VoiceType.Uno.Presentation.SettingsUi;

/// <summary>
/// Builds the rows, cards and notes the Settings sections are made of, in the look of Settings › History
/// (<see cref="HistoryStyles"/>): 8 px cards with a 1 px stroke, 56 px rows, every control at least 44 px tall.
/// </summary>
internal static class SettingsRows
{
    private static HistoryStyles? s_styles;

    private static HistoryStyles Styles => s_styles ??= new HistoryStyles();

    public static Style StyleOf(string key) => (Style)Styles[key];

    /// <summary>A brush from the History theme dictionaries for the theme the element is shown in.</summary>
    public static Brush ThemeBrush(FrameworkElement scope, string key)
    {
        var theme = scope.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
        return (Brush)((ResourceDictionary)Styles.ThemeDictionaries[theme])[key];
    }

    /// <summary>A system theme brush (card stroke, secondary text...) for the element's theme.</summary>
    public static Brush SystemBrush(string key)
    {
        try
        {
            if (Application.Current.Resources.TryGetValue(key, out var value) && value is Brush brush)
                return brush;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VoiceType.Uno] Theme brush {key} unavailable: {ex.Message}");
        }
        return new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    public static FontFamily MonoFont { get; } = new(OperatingSystem.IsWindows() ? "Consolas" : OperatingSystem.IsMacOS() ? "Menlo" : OperatingSystem.IsAndroid() ? "monospace" : "DejaVu Sans Mono");

    public static TextBlock SectionHeader(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        Style = StyleOf("HistorySectionHeaderStyle"),
    };

    public static TextBlock Secondary(string text, double fontSize = 12) => new()
    {
        Text = text,
        Style = StyleOf("HistorySecondaryStyle"),
        FontSize = fontSize,
    };

    /// <summary>The line under a group (design "note").</summary>
    public static TextBlock Note(string text) => new()
    {
        Text = text,
        Style = StyleOf("HistorySecondaryStyle"),
        Margin = new Thickness(4, 8, 4, 0),
    };

    /// <summary>A card holding rows; a hairline divider goes between them.</summary>
    public static Border Card(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (var i = 0; i < rows.Length; i++)
        {
            if (i > 0)
                stack.Children.Add(Divider());
            stack.Children.Add(rows[i]);
        }
        return new Border { Style = StyleOf("HistoryCardStyle"), Child = stack };
    }

    public static Border Divider() => new()
    {
        Height = 1,
        Margin = new Thickness(12, 0, 12, 0),
        Background = SystemBrush("CardStrokeColorDefaultBrush"),
    };

    public static SymbolIcon Icon(Symbol symbol) => new(symbol) { VerticalAlignment = VerticalAlignment.Center };

    private static Grid RowGrid(UIElement icon, bool withControl)
    {
        var grid = new Grid { MinHeight = 56, Padding = new Thickness(12, 6, 12, 6), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (withControl)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn((FrameworkElement)icon, 0);
        grid.Children.Add(icon);
        return grid;
    }

    public static StackPanel TitleBlock(string title, string? detail, out TextBlock detailBlock)
    {
        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        detailBlock = Secondary(detail ?? "");
        detailBlock.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        panel.Children.Add(detailBlock);
        return panel;
    }

    /// <summary>Icon, title and detail on the left, a control (switch, combo box) on the right.</summary>
    public static Grid Row(UIElement icon, string title, string? detail, FrameworkElement? control = null)
        => Row(icon, title, detail, control, out _);

    public static Grid Row(UIElement icon, string title, string? detail, FrameworkElement? control, out TextBlock detailBlock)
    {
        var grid = RowGrid(icon, control is not null);
        var text = TitleBlock(title, detail, out detailBlock);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (control is not null)
        {
            Grid.SetColumn(control, 2);
            control.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(control);
        }
        return grid;
    }

    /// <summary>Icon, title and detail, with a full-width control below them (text boxes, segmented choices).</summary>
    public static Grid StackRow(UIElement icon, string title, string? detail, FrameworkElement control, out TextBlock detailBlock)
    {
        var grid = RowGrid(icon, false);
        grid.RowSpacing = 8;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = TitleBlock(title, detail, out detailBlock);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Grid.SetRow(control, 1);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A whole-row button with a chevron (links to other pages).</summary>
    public static Button LinkRow(UIElement icon, string title, string? detail, Action onClick, string? automationName = null)
        => LinkRow(icon, title, detail, onClick, out _, automationName);

    public static Button LinkRow(UIElement icon, string title, string? detail, Action onClick, out TextBlock detailBlock, string? automationName = null)
    {
        var grid = RowGrid(icon, true);
        grid.MinHeight = 44;
        grid.Padding = new Thickness(0);
        var text = TitleBlock(title, detail, out detailBlock);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var chevron = Chevron();
        Grid.SetColumn(chevron, 2);
        grid.Children.Add(chevron);

        var button = new Button
        {
            Style = StyleOf("HistoryRowButtonStyle"),
            MinHeight = 56,
            Content = grid,
        };
        AutomationProperties.SetName(button, automationName ?? title);
        button.Click += (_, _) => onClick();
        return button;
    }

    public static FontIcon Chevron() => new()
    {
        Glyph = "",
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = SystemBrush("TextFillColorSecondaryBrush"),
    };

    public static ToggleSwitch Switch(bool isOn, string automationName, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { Style = StyleOf("HistorySwitchStyle"), IsOn = isOn };
        AutomationProperties.SetName(toggle, automationName);
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return toggle;
    }

    /// <summary>A combo box over <paramref name="items"/>; <paramref name="selected"/> is an index.</summary>
    public static ComboBox Combo(IReadOnlyList<string> items, int selected, string automationName, Action<int> changed, double minWidth = 112)
    {
        var combo = new ComboBox { MinHeight = 44, MinWidth = minWidth };
        foreach (var item in items)
            combo.Items.Add(item);
        combo.SelectedIndex = selected;
        AutomationProperties.SetName(combo, automationName);
        var loaded = false;
        combo.SelectionChanged += (_, _) =>
        {
            if (loaded && combo.SelectedIndex >= 0)
                changed(combo.SelectedIndex);
        };
        loaded = true;
        return combo;
    }

    /// <summary>
    /// A radio row (title, optional tag chip, detail). Radios of one group share <paramref name="group"/>; unavailable ones
    /// are shown disabled with their explanation.
    /// </summary>
    public static RadioButton Radio(FrameworkElement scope, string group, SettingsOption option, bool isChecked, Action<string> chosen)
    {
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = option.Title, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        if (!string.IsNullOrEmpty(option.Tag))
        {
            var chip = new Border
            {
                Style = StyleOf("HistoryChipStyle"),
                Background = ThemeBrush(scope, "HistoryHintBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = option.Tag, Style = StyleOf("HistoryChipTextStyle") },
            };
            head.Children.Add(chip);
        }
        content.Children.Add(head);
        if (!string.IsNullOrEmpty(option.Detail))
            content.Children.Add(Secondary(option.Detail));

        var radio = new RadioButton
        {
            GroupName = group,
            Content = content,
            IsChecked = isChecked,
            IsEnabled = option.Enabled,
            MinHeight = 56,
            MinWidth = 0,
            Padding = new Thickness(8, 6, 12, 6),
            Margin = new Thickness(10, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(radio, option.Title);
        radio.Checked += (_, _) => chosen(option.Value);
        return radio;
    }

    /// <summary>Two or more choices side by side in one row (Processor | Graphics card, repetition penalty).</summary>
    public static Border Segmented(FrameworkElement scope, IReadOnlyList<SettingsOption> options, string selectedValue, string group, Action<string> chosen)
    {
        var grid = new Grid { ColumnSpacing = 2 };
        for (var i = 0; i < options.Count; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var option = options[i];
            var radio = new RadioButton
            {
                Style = StyleOf("HistorySegmentStyle"),
                GroupName = group,
                Content = option.Title,
                IsChecked = string.Equals(option.Value, selectedValue, StringComparison.Ordinal),
            };
            AutomationProperties.SetName(radio, option.Title);
            radio.Checked += (_, _) => chosen(option.Value);
            Grid.SetColumn(radio, i);
            grid.Children.Add(radio);
        }
        return new Border
        {
            Background = SystemBrush("ControlFillColorSecondaryBrush"),
            BorderBrush = SystemBrush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = grid,
        };
    }

    /// <summary>A text box in the monospace look of a path or address.</summary>
    public static TextBox MonoBox(string text, string automationName, string placeholder = "")
    {
        var box = new TextBox
        {
            Text = text,
            PlaceholderText = placeholder,
            MinHeight = 44,
            FontFamily = MonoFont,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(box, automationName);
        return box;
    }

    /// <summary>
    /// An info or warning note with an optional row of buttons. <paramref name="warn"/> uses the amber look of
    /// "Models were not moved" and "shortcut taken".
    /// </summary>
    public static Border Callout(FrameworkElement scope, string text, bool warn, params (string Label, Action Click)[] buttons)
    {
        var content = new StackPanel { Spacing = 8 };
        var textBlock = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13, IsTextSelectionEnabled = true };
        content.Children.Add(textBlock);
        if (buttons.Length > 0)
        {
            var row = new Grid { ColumnSpacing = 8 };
            for (var i = 0; i < buttons.Length; i++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var (label, click) = buttons[i];
                var button = new Button
                {
                    Style = StyleOf(i == 0 ? "HistoryAccentButtonStyle" : "HistoryActionButtonStyle"),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                };
                AutomationProperties.SetName(button, label);
                button.Click += (_, _) => click();
                Grid.SetColumn(button, i);
                row.Children.Add(button);
            }
            content.Children.Add(row);
        }

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new FontIcon { Glyph = warn ? "" : "", FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) };
        grid.Children.Add(icon);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);

        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Background = ThemeBrush(scope, warn ? "HistoryWarningChipBrush" : "HistoryHintBrush"),
            Child = grid,
        };
        if (warn)
        {
            border.BorderBrush = ThemeBrush(scope, "HistoryWarningStrokeBrush");
            border.BorderThickness = new Thickness(1);
        }
        return border;
    }

    /// <summary>A path in a selectable monospace box that wraps and is never cut with an ellipsis.</summary>
    public static Border PathBox(string path, string automationName)
    {
        var text = new TextBlock
        {
            Text = path,
            FontFamily = MonoFont,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        AutomationProperties.SetName(text, automationName);
        return new Border
        {
            Background = SystemBrush("ControlFillColorSecondaryBrush"),
            BorderBrush = SystemBrush("ControlStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = text,
        };
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }
}
