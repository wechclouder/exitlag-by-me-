using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace wetwiLAG.Infrastructure;

public sealed class StateVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isVisible = value switch
        {
            bool boolean => boolean,
            int count => count > 0,
            _ => value is not null
        };
        if (string.Equals(parameter?.ToString(), "Invert", StringComparison.OrdinalIgnoreCase))
        {
            isVisible = !isVisible;
        }

        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string color || ColorConverter.ConvertFromString(color) is not Color parsed)
        {
            return Brushes.Transparent;
        }

        var brush = new SolidColorBrush(parsed);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is SolidColorBrush brush ? brush.Color.ToString() : "";
}

public sealed class SectionVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
