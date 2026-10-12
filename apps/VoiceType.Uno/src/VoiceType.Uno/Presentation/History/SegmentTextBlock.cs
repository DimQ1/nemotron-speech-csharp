using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using VoiceType.Uno.History;

namespace VoiceType.Uno.Presentation.History;

/// <summary>
/// Text of one transcript segment (a user control around a wrapping <see cref="TextBlock"/>; WinUI's TextBlock is sealed).
/// Without speaker labels it is plain text; with a layer a speaker change inside the segment shows an inline
/// <c>● Name</c> marker, and "colour words" tints every word by its speaker. Rebuilds itself when the view
/// model's speaker state or the theme changes. <see cref="Control.FontSize"/> flows to the text.
/// </summary>
public sealed partial class SegmentTextBlock : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(SegmentViewModel), typeof(SegmentTextBlock),
        new PropertyMetadata(null, (d, e) => ((SegmentTextBlock)d).OnSourceChanged(e.OldValue as SegmentViewModel, e.NewValue as SegmentViewModel)));

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };

    public SegmentTextBlock()
    {
        IsTabStop = false;
        Content = _text;
        Unloaded += (_, _) =>
        {
            if (Source is { } s)
                s.PropertyChanged -= OnSourceProperty;
        };
        Loaded += (_, _) =>
        {
            if (Source is { } s)
            {
                s.PropertyChanged -= OnSourceProperty;
                s.PropertyChanged += OnSourceProperty;
            }
            Rebuild();
        };
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public SegmentViewModel? Source
    {
        get => (SegmentViewModel?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private void OnSourceChanged(SegmentViewModel? old, SegmentViewModel? next)
    {
        if (old is not null)
            old.PropertyChanged -= OnSourceProperty;
        if (next is not null)
            next.PropertyChanged += OnSourceProperty;
        Rebuild();
    }

    private void OnSourceProperty(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SegmentViewModel.SpeakerStamp))
            Rebuild();
    }

    private void Rebuild()
    {
        _text.Inlines.Clear();
        var segment = Source;
        if (segment is null)
        {
            _text.Text = "";
            return;
        }
        var runs = segment.TextRuns;
        if (runs.Count == 0)
        {
            _text.Text = segment.Text;
            return;
        }
        _text.Text = "";
        foreach (var run in runs)
        {
            var inline = new Run { Text = run.Text };
            if (run.ColorIndex is { } color)
                inline.Foreground = SpeakerServices.BrushFor(color, this);
            if (run.IsMarker)
                inline.FontWeight = FontWeights.SemiBold;
            _text.Inlines.Add(inline);
        }
    }
}
