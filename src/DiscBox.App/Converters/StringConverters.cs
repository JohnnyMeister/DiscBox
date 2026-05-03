using Avalonia.Data.Converters;
using System.Globalization;

namespace DiscBox.Converters;

public static class StringConverters
{
    public static readonly IValueConverter IsNotNullOrEmpty =
        new FuncValueConverter<string?, bool>(value => !string.IsNullOrEmpty(value));
}
