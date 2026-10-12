using Windows.Foundation;

namespace VoiceType.Uno.Presentation.Controls;

/// <summary>
/// Model cards in one column on phones and two side by side when there is room
/// (at least <see cref="MinColumnWidth"/> per column). Cards in the same row are
/// stretched to the taller one, so the action rows line up. Unlike
/// UniformGridLayout, rows size to their own content, so a card with a progress
/// bar or expanded details is never clipped.
/// </summary>
public sealed partial class CardColumnsPanel : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(CardColumnsPanel),
        new PropertyMetadata(360d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(CardColumnsPanel),
        new PropertyMetadata(2, OnLayoutPropertyChanged));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(CardColumnsPanel),
        new PropertyMetadata(8d, OnLayoutPropertyChanged));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>Gap between columns and between rows.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CardColumnsPanel)d).InvalidateMeasure();

    private int ColumnsFor(double width)
    {
        if (double.IsInfinity(width) || width <= 0)
            return 1;

        var fit = (int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing));
        return Math.Clamp(fit, 1, Math.Max(1, MaxColumns));
    }

    private double ColumnWidth(double width, int columns)
        => double.IsInfinity(width) ? double.PositiveInfinity : Math.Max(0, (width - Spacing * (columns - 1)) / columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnsFor(availableSize.Width);
        var columnWidth = ColumnWidth(availableSize.Width, columns);

        double height = 0, rowHeight = 0, widest = 0;
        var index = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            widest = Math.Max(widest, child.DesiredSize.Width);
            rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

            if (++index % columns == 0)
            {
                height += rowHeight + Spacing;
                rowHeight = 0;
            }
        }

        if (index % columns != 0)
            height += rowHeight;
        else if (index > 0)
            height -= Spacing;

        var width = double.IsInfinity(availableSize.Width) ? widest : availableSize.Width;
        return new Size(width, Math.Max(0, height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = ColumnsFor(finalSize.Width);
        var columnWidth = ColumnWidth(finalSize.Width, columns);
        var children = Children.ToList();

        double y = 0;
        for (var row = 0; row * columns < children.Count; row++)
        {
            var cells = children.Skip(row * columns).Take(columns).ToList();
            var rowHeight = cells.Max(child => child.DesiredSize.Height);

            for (var column = 0; column < cells.Count; column++)
                cells[column].Arrange(new Rect(column * (columnWidth + Spacing), y, columnWidth, rowHeight));

            y += rowHeight + Spacing;
        }

        return finalSize;
    }
}
