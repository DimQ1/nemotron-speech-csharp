using Windows.Foundation;

namespace VoiceType.Uno.Presentation.Controls;

/// <summary>
/// Lays children out left to right and wraps to a new line when the row is full,
/// so a row of chips never overflows a narrow card. Collapsed children take no space.
/// (WinUI has no WrapPanel and the app does not reference the Community Toolkit.)
/// </summary>
public sealed partial class ChipWrapPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ChipWrapPanel),
        new PropertyMetadata(4d, (d, _) => ((ChipWrapPanel)d).InvalidateMeasure()));

    /// <summary>Gap between chips, horizontally and between lines.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var childConstraint = new Size(availableSize.Width, double.PositiveInfinity);
        foreach (var child in Children)
            child.Measure(childConstraint);

        return Flow(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Flow(finalSize.Width, arrange: true);
        return finalSize;
    }

    /// <summary>Walks the children line by line; arranges them when asked and returns the extent.</summary>
    private Size Flow(double maxWidth, bool arrange)
    {
        double x = 0, y = 0, lineHeight = 0, extentWidth = 0;
        var any = false;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            var size = child.DesiredSize;
            if (any && x > 0 && x + size.Width > maxWidth)
            {
                y += lineHeight + Spacing;
                x = 0;
                lineHeight = 0;
            }

            if (arrange)
                child.Arrange(new Rect(x, y, Math.Min(size.Width, double.IsInfinity(maxWidth) ? size.Width : maxWidth), size.Height));

            extentWidth = Math.Max(extentWidth, x + size.Width);
            x += size.Width + Spacing;
            lineHeight = Math.Max(lineHeight, size.Height);
            any = true;
        }

        return any ? new Size(Math.Min(extentWidth, maxWidth), y + lineHeight) : new Size(0, 0);
    }
}
