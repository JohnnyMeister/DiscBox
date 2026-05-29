using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace DiscBox.Converters;

public class SelectionBackgroundConverter : IValueConverter
{
    private static readonly IBrush Selected = new SolidColorBrush(Color.Parse("#123947"));
    private static readonly IBrush Normal = Brushes.Transparent;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is true ? Selected : Normal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
