using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VoiceType.Converters;

/// <summary>Returns Visibility.Visible when the int value equals the int parameter.</summary>
public sealed class IntEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int tabIndex && parameter is string paramStr && int.TryParse(paramStr, out int target))
            return tabIndex == target ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns Visibility.Visible when the int value is greater than 0.</summary>
public sealed class PercentToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int percent && percent > 0)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
