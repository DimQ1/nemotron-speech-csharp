using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.Chat;

/// <summary>
/// Renders an answer's markdown subset (paragraphs, bullets, headings, bold, italic, code) as
/// selectable <see cref="TextBlock"/>s. Rebuilt on every text change, which is cheap for answers of a
/// few hundred words; the parsing itself lives in <see cref="ChatMarkdown"/> (Core, tested).
/// </summary>
public sealed partial class ChatTextView : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ChatTextView),
        new PropertyMetadata("", (d, _) => ((ChatTextView)d).Rebuild()));

    private readonly StackPanel _panel = new() { Spacing = 6 };

    public ChatTextView()
    {
        IsTabStop = false;
        Content = _panel;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        foreach (var block in ChatMarkdown.Parse(Text))
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                FontSize = 15,
                LineHeight = 22,
            };
            foreach (var run in block.Runs)
                textBlock.Inlines.Add(ToRun(run, block.Kind == ChatMdBlockKind.Heading));

            if (block.Kind == ChatMdBlockKind.Bullet)
            {
                var row = new Grid { ColumnSpacing = 8 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = "•", FontSize = 15, LineHeight = 22, HorizontalAlignment = HorizontalAlignment.Center });
                Grid.SetColumn(textBlock, 1);
                row.Children.Add(textBlock);
                _panel.Children.Add(row);
            }
            else
            {
                _panel.Children.Add(textBlock);
            }
        }
    }

    private static Run ToRun(ChatMdRun run, bool heading)
    {
        var result = new Run { Text = run.Text };
        if (heading || run.Style == ChatMdStyle.Bold)
            result.FontWeight = FontWeights.SemiBold;
        if (run.Style == ChatMdStyle.Italic)
            result.FontStyle = Windows.UI.Text.FontStyle.Italic;
        if (run.Style == ChatMdStyle.Code)
            result.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Menlo, monospace");
        return result;
    }
}
