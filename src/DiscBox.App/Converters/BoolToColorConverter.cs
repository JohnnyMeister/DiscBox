using Avalonia.Data.Converters;
using Avalonia.Media;
using System.Globalization;

namespace DiscBox.Converters;

public class BoolToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        if (value is bool boolValue)
        {
            return boolValue ? new SolidColorBrush(Color.Parse("#ff6b6b")) : new SolidColorBrush(Color.Parse("#e3e5e8"));
        }
        return new SolidColorBrush(Color.Parse("#e3e5e8"));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo? culture)
    {
        throw new NotImplementedException();
    }
}
