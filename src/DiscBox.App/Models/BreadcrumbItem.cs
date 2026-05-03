namespace DiscBox.Models;

/// <summary>
/// Represents one segment in the breadcrumb navigation bar.
/// Replaces the (string Label, string Path) tuple, which doesn't support
/// Avalonia compiled bindings (x:DataType).
/// </summary>
public class BreadcrumbItem
{
    public string Label { get; set; } = string.Empty;
    public string Path  { get; set; } = string.Empty;
}
