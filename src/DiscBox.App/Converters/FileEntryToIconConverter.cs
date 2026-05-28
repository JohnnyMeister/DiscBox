using Avalonia.Data.Converters;
using DiscBox.Models;
using Material.Icons;
using System;
using System.Globalization;

namespace DiscBox.Converters;

public class FileEntryToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is FileEntry entry)
        {
            if (entry.IsFolder) return MaterialIconKind.Folder;
            if (entry.IsImage) return MaterialIconKind.Image;
            if (entry.IsVideo) return MaterialIconKind.Video;
            
            if (entry.MimeType == "application/pdf") return MaterialIconKind.FilePdfBox;
            if (entry.MimeType?.StartsWith("audio/") == true) return MaterialIconKind.MusicBox;
            if (entry.MimeType?.StartsWith("text/") == true) return MaterialIconKind.TextBox;
            if (entry.MimeType?.Contains("zip") == true || entry.MimeType?.Contains("rar") == true) return MaterialIconKind.ZipBox;

            return MaterialIconKind.FileDocumentOutline;
        }

        return MaterialIconKind.FileOutline;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
