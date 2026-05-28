using Avalonia.Data.Converters;
using Avalonia.Media;
using DiscBox.Models;
using System;
using System.Globalization;

namespace DiscBox.Converters;

public class FileEntryToColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is FileEntry entry)
        {
            if (entry.IsFolder) return new SolidColorBrush(Color.Parse("#6A0DAD")); // Purple
            if (entry.IsImage || entry.IsVideo) return new SolidColorBrush(Color.Parse("#00E5FF")); // Cyan
            
            return new SolidColorBrush(Color.Parse("#4A90E2")); // Blue
        }

        return new SolidColorBrush(Color.Parse("#8E9297"));
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
